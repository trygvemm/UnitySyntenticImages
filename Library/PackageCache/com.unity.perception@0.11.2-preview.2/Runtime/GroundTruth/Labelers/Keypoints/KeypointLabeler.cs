using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Perception.GroundTruth.DataModel;
using UnityEngine.Rendering;

namespace UnityEngine.Perception.GroundTruth
{
    /// <summary>
    /// Produces keypoint annotations for a humanoid model. This labeler supports generic
    /// <see cref="KeypointTemplate"/>. Template values are mapped to rigged
    /// <see cref="Animator"/> <seealso cref="Avatar"/>. Custom joints can be
    /// created by applying <see cref="JointLabel"/> to empty game objects at a body
    /// part's location.
    ///
    /// Keypoints are recorded by this labeler with a state value describing if they are present on the model,
    /// present but not visible, or visible. A keypoint can be listed as not visible for three reasons: it is outside
    /// of the camera's view frustum, it is occluded by another object in the scene, or it is occluded by itself, for
    /// example a raised arm in front a model's face could occlude its eyes from being visible. To calculate self
    /// occlusion values, the keypoint labeler uses tolerances per keypoint to determine if the keypoint is blocked.
    /// The initial tolerance value for each keypoint is set per keypoint in the <see cref="KeypointTemplate"/> file.
    /// The tolerance of a custom keypoints can be set with the <see cref="JointLabel"/> used to create the keypoint.
    /// Finally, a <see cref="KeypointOcclusionOverrides"/> component be added to a model to apply a universal scaling
    /// override to all of the keypoint tolerances defined in a keypoint template.
    /// </summary>
    [Serializable]
    public sealed class KeypointLabeler : CameraLabeler
    {
        // Smaller texture sizes produce assertion failures in the engine
        const int k_MinTextureWidth = 8;

        static ProfilerMarker s_OnEndRenderingMarker = new ProfilerMarker($"KeypointLabeler OnEndRendering");
        static ProfilerMarker s_OnVisualizeMarker = new ProfilerMarker($"KeypointLabeler OnVisualize");

        /// <summary>
        /// The active keypoint template. Required to annotate keypoint data.
        /// </summary>
        public KeypointTemplate activeTemplate;

        /// <inheritdoc/>
        public override string description => KeypointAnnotationDefinition.labelerDescription;

        ///<inheritdoc/>
        protected override bool supportsVisualization => true;

        // ReSharper disable MemberCanBePrivate.Global
        /// <summary>
        /// The GUID id to associate with the annotations produced by this labeler.
        /// </summary>
        public string annotationId = "keypoints";

        /// <inheritdoc />
        public override string labelerId => annotationId;

        /// <summary>
        /// The <see cref="IdLabelConfig"/> which associates objects with labels.
        /// </summary>
        public IdLabelConfig idLabelConfig;

        /// <summary>
        /// Controls which objects will have keypoints recorded in the dataset.
        /// <see cref="KeypointObjectFilter"/>
        /// </summary>
        public KeypointObjectFilter objectFilter;
        // ReSharper restore MemberCanBePrivate.Global

        AnnotationDefinition m_AnnotationDefinition;
        Texture2D m_MissingTexture;
        Material m_MaterialDepthCheck;
        Texture2D m_KeypointPositionsTexture;
        Texture2D m_KeypointCheckDepthTexture;

        struct FrameKeypointData
        {
            public AsyncFuture<Annotation> annotation;
            public int pointsPerEntry;
            public List<KeypointComponent> keypoints;
            public bool isDepthCheckComplete;
            public bool isInstanceSegmentationCheckComplete;
            public NativeArray<RenderedObjectInfo> objectInfos;
        }

        Dictionary<int, FrameKeypointData> m_FrameKeypointData;
        List<KeypointComponent> m_LatestReported;

        int m_CurrentFrame;

        /// <summary>
        /// Action that gets triggered when a new frame of key points are computed.
        /// </summary>
        public event Action<int, List<KeypointComponent>> KeypointsComputed;

        /// <summary>
        /// Creates a new key point labeler. This constructor creates a labeler that
        /// is not valid until a <see cref="IdLabelConfig"/> and <see cref="KeypointTemplate"/>
        /// are assigned.
        /// </summary>
        public KeypointLabeler() { }

        /// <summary>
        /// Creates a new key point labeler.
        /// </summary>
        /// <param name="config">The Id label config for the labeler</param>
        /// <param name="template">The active keypoint template</param>
        public KeypointLabeler(IdLabelConfig config, KeypointTemplate template)
        {
            this.idLabelConfig = config;
            this.activeTemplate = template;
        }

        /// <summary>
        /// Array of animation pose labels which map animation clip times to ground truth pose labels.
        /// </summary>
        public List<AnimationPoseConfig> animationPoseConfigs;

        ComputeShader m_KeypointDepthTestShader;
        RenderTexture m_ResultsBuffer;

        /// <inheritdoc/>
        protected override void Setup()
        {
            if (idLabelConfig == null)
                throw new InvalidOperationException($"{nameof(KeypointLabeler)}'s idLabelConfig field must be assigned");

            m_AnnotationDefinition = new KeypointAnnotationDefinition(annotationId, TemplateToJson(activeTemplate, idLabelConfig));
            DatasetCapture.RegisterAnnotationDefinition(m_AnnotationDefinition);

            visualizationEnabled = supportsVisualization;

            // Texture to use in case the template does not contain a texture for the joints or the skeletal connections
            m_MissingTexture = new Texture2D(1, 1);

            m_KnownStatus = new Dictionary<uint, CachedData>();

            m_FrameKeypointData = new Dictionary<int, FrameKeypointData>();
            m_CurrentFrame = 0;
            m_KeypointDepthTestShader = (ComputeShader) Resources.Load("KeypointDepthTest");

            var depthCheckShader = Shader.Find("Perception/KeypointDepthCheck");

            var shaderVariantCollection = new ShaderVariantCollection();
            m_MaterialDepthCheck = new Material(depthCheckShader);

            shaderVariantCollection.Add(
                new ShaderVariantCollection.ShaderVariant(depthCheckShader, PassType.ScriptableRenderPipeline));
            shaderVariantCollection.WarmUp();

            perceptionCamera.attachedCamera.depthTextureMode = DepthTextureMode.Depth;
#if URP_PRESENT
            var cameraData = Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(perceptionCamera.attachedCamera);
            cameraData.requiresDepthOption = Rendering.Universal.CameraOverrideOption.On;
            cameraData.requiresDepthTexture = true;
#endif

            perceptionCamera.InstanceSegmentationImageReadback += OnInstanceSegmentationImageReadback;
            perceptionCamera.RenderedObjectInfosCalculated += OnRenderedObjectInfoReadback;
        }

        void SetupDepthCheckBuffers(int size)
        {
            var textureDimensions = TextureDimensions(size);
            if (m_ResultsBuffer != null &&
                textureDimensions.x == m_ResultsBuffer.width &&
                textureDimensions.y == m_ResultsBuffer.height)
                return;


            if (m_ResultsBuffer != null)
            {
                m_ResultsBuffer.Release();
            }

            m_KeypointPositionsTexture = new Texture2D(textureDimensions.x, textureDimensions.y, GraphicsFormat.R16G16_SFloat, TextureCreationFlags.None);
            m_KeypointCheckDepthTexture = new Texture2D(textureDimensions.x, textureDimensions.y, GraphicsFormat.R16_SFloat, TextureCreationFlags.None);

            m_ResultsBuffer = new RenderTexture(textureDimensions.x, textureDimensions.y, 0, GraphicsFormat.R8G8B8A8_UNorm);
        }

        bool AreEqual(Color32 lhs, Color32 rhs)
        {
            return lhs.r == rhs.r && lhs.g == rhs.g && lhs.b == rhs.b && lhs.a == rhs.a;
        }

        bool PixelOnScreen(int2 pixelLocation, (int x, int y) dimensions)
        {
            return pixelLocation.x >= 0 && pixelLocation.x < dimensions.x && pixelLocation.y >= 0 && pixelLocation.y < dimensions.y;
        }

        bool PixelsMatch(int x, int y, Color32 idColor, (int x, int y) dimensions, NativeArray<Color32> data)
        {
            var h = dimensions.y - 1 - y;
            var pixelColor = data[h * dimensions.x + x];
            return AreEqual(pixelColor, idColor);
        }

        static int s_PixelTolerance = 1;

        // Determine the state of a keypoint. A keypoint is considered visible (state = 2) if it is on screen and not occluded
        // by itself or another object. Self-occlusion has already been checked, so the input keypoint may be state 2, 1, or 0.
        // We determine if a point is occluded by other objects is by checking the pixel location of the keypoint
        // against the instance segmentation mask for the frame. The instance segmentation mask provides the instance id of the
        // visible object at a pixel location. Which means, if the keypoint does not match the visible pixel, then another
        // object is in front of the keypoint occluding it from view. An important note here is that the keypoint is an infinitely small
        // point in space, which can lead to false negatives due to rounding issues if the keypoint is on the edge of an object or very
        // close to the edge of the screen. Because of this we will test not only the keypoint pixel, but also the immediate surrounding
        // pixels  to determine if the pixel is really visible. This method returns 1 if the pixel is not visible but on screen, and 0
        // if the pixel is off of the screen (taken the tolerance into account).
        int DetermineKeypointState(KeypointValue keypoint, Color32 instanceIdColor, (int x, int y) dimensions, NativeArray<Color32> data)
        {
            if (keypoint.state == 0) return 0;

            var pixelLocation = PixelLocationFromScreenPoint(keypoint);

            if (!PixelOnScreen(pixelLocation, dimensions))
                return 0;

            var pixelMatched = false;

            for (var y = pixelLocation.y - s_PixelTolerance; y <= pixelLocation.y + s_PixelTolerance; y++)
            {
                for (var x = pixelLocation.x - s_PixelTolerance; x <= pixelLocation.x + s_PixelTolerance; x++)
                {
                    if (!PixelOnScreen(new int2(x, y), dimensions)) continue;

                    pixelMatched = true;
                    if (PixelsMatch(x, y, instanceIdColor, dimensions, data))
                    {
                        return keypoint.state;
                    }
                }
            }

            return pixelMatched ? 1 : 0;
        }

        static int2 PixelLocationFromScreenPoint(KeypointValue keypoint)
        {
            var centerX = Mathf.FloorToInt(keypoint.location.x);
            var centerY = Mathf.FloorToInt(keypoint.location.y);
            var pixelLocation = new int2(centerX, centerY);
            return pixelLocation;
        }

        void OnInstanceSegmentationImageReadback(int frameCount, NativeArray<Color32> data, RenderTexture renderTexture)
        {
            if (!m_FrameKeypointData.TryGetValue(frameCount, out var frameKeypointData))
                return;

            var dimensions = (renderTexture.width, renderTexture.height);

            foreach (var keypointEntry in frameKeypointData.keypoints)
            {
                if (InstanceIdToColorMapping.TryGetColorFromInstanceId(keypointEntry.instanceId, out var idColor))
                {
                    for (var i = 0; i < keypointEntry.keypoints.Length; i++)
                    {
                        var keypoint = keypointEntry.keypoints[i];
                        keypoint.state = DetermineKeypointState(keypoint, idColor, dimensions, data);

                        if (keypoint.state == 0)
                        {
                            keypoint.location = Vector2.zero;
                        }
                        else
                        {
                            var location = keypoint.location;
                            location.x = math.clamp(keypoint.location.x, 0, dimensions.width - .001f);
                            location.y = math.clamp(keypoint.location.y, 0, dimensions.height - .001f);
                            keypoint.location = location;
                        }

                        keypointEntry.keypoints[i] = keypoint;
                    }
                }
            }

            frameKeypointData.isInstanceSegmentationCheckComplete = true;
            m_FrameKeypointData[frameCount] = frameKeypointData;
            ReportIfComplete(frameCount, frameKeypointData);
        }

        void OnRenderedObjectInfoReadback(int frameCount, NativeArray<RenderedObjectInfo> objectInfos)
        {
            if (!m_FrameKeypointData.TryGetValue(frameCount, out var frameKeypointData))
                return;

            frameKeypointData.objectInfos = new NativeArray<RenderedObjectInfo>(objectInfos, Allocator.Persistent);
            m_FrameKeypointData[frameCount] = frameKeypointData;
            ReportIfComplete(frameCount, frameKeypointData);
        }

        void ReportIfComplete(int frameCount, FrameKeypointData frameKeypointData)
        {
            if (!frameKeypointData.isInstanceSegmentationCheckComplete || !frameKeypointData.isDepthCheckComplete || !frameKeypointData.objectInfos.IsCreated)
                return;

            var reportList = new List<KeypointComponent>();

            //filter out objects that are not visible
            foreach (var entry in frameKeypointData.keypoints)
            {
                var include = false;
                if (objectFilter == KeypointObjectFilter.All)
                    include = true;
                else
                {
                    foreach (var objectInfo in frameKeypointData.objectInfos)
                    {
                        if (entry.instanceId == objectInfo.instanceId)
                        {
                            include = true;
                            break;
                        }
                    }

                    if (!include && objectFilter == KeypointObjectFilter.VisibleAndOccluded)
                        include = entry.keypoints.Any(k => k.state == 1);
                }

                if (include)
                    reportList.Add(entry);
            }
            m_FrameKeypointData.Remove(frameCount);
            KeypointsComputed?.Invoke(frameCount, reportList);
            var toReport = new KeypointAnnotation(m_AnnotationDefinition, perceptionCamera.ID, activeTemplate.templateID, reportList);
            frameKeypointData.annotation.Report(toReport);
            frameKeypointData.objectInfos.Dispose();
            m_LatestReported = reportList;
        }

        /// <inheritdoc/>
        protected override void OnEndRendering(ScriptableRenderContext scriptableRenderContext)
        {
            using (s_OnEndRenderingMarker.Auto())
            {
                m_CurrentFrame = Time.frameCount;

                var annotation = perceptionCamera.SensorHandle.ReportAnnotationAsync(m_AnnotationDefinition);
                var keypointEntries = new List<KeypointComponent>();
                var checkLocations = new NativeList<float3>(512, Allocator.Persistent);

                foreach (var label in LabelManager.singleton.registeredLabels)
                    ProcessLabel(label, keypointEntries, checkLocations);

                m_FrameKeypointData[m_CurrentFrame] = new FrameKeypointData
                {
                    annotation = annotation,
                    keypoints = keypointEntries,
                    pointsPerEntry = activeTemplate.keypoints.Length
                };

                if (keypointEntries.Count != 0)
                    DoDepthCheck(scriptableRenderContext, keypointEntries, checkLocations);
                else
                {
                    var frameKeypointData = m_FrameKeypointData[m_CurrentFrame];
                    frameKeypointData.isDepthCheckComplete = true;
                    m_FrameKeypointData[m_CurrentFrame] = frameKeypointData;
                }

                checkLocations.Dispose();
            }
        }

        /// Check self occlusion of each keypoint by passing keypoint location (x & y in one texture) and modified distance from camera (keypoint distance - keypoint threshold distance)
        /// in an additional texture. The computer shader checks the depth buffer at each passed in location, converts the depth at the pixel to linear space, and then compares it to
        /// the passed in modified keypoint distance. If the modified keypoint distance is less than the depth buffer distance, the keypoint is visible, else it is blocked by itself.
        void DoDepthCheck(ScriptableRenderContext scriptableRenderContext, List<KeypointComponent> keypointEntries, NativeList<float3> checkLocations)
        {
            var keypointCount = keypointEntries.Count * activeTemplate.keypoints.Length;

            var commandBuffer = CommandBufferPool.Get("KeypointDepthCheck");

            var textureDimensions = TextureDimensions(keypointCount);


            SetupDepthCheckBuffers(checkLocations.Length);

            var positionsPixeldata = new NativeArray<half>(textureDimensions.x * textureDimensions.y * 2, Allocator.Temp);
            var depthPixeldata = new NativeArray<half>(textureDimensions.x * textureDimensions.y, Allocator.Temp);

            for (int i = 0; i < checkLocations.Length; i++)
            {
                var pos = checkLocations[i];
                positionsPixeldata[i * 2] = new half(pos.x);
                positionsPixeldata[i * 2 + 1] = new half(pos.y);
                depthPixeldata[i] = new half(pos.z);
            }

            m_KeypointPositionsTexture.SetPixelData(positionsPixeldata, 0);
            m_KeypointPositionsTexture.Apply();
            m_KeypointCheckDepthTexture.SetPixelData(depthPixeldata, 0);
            m_KeypointCheckDepthTexture.Apply();

            positionsPixeldata.Dispose();
            depthPixeldata.Dispose();

            m_MaterialDepthCheck.SetTexture("_Positions", m_KeypointPositionsTexture);
            m_MaterialDepthCheck.SetTexture("_KeypointCheckDepth", m_KeypointCheckDepthTexture);
            commandBuffer.Blit(null, m_ResultsBuffer, m_MaterialDepthCheck);

            scriptableRenderContext.ExecuteCommandBuffer(commandBuffer);
            scriptableRenderContext.Submit();
            CommandBufferPool.Release(commandBuffer);
            RenderTextureReader.Capture<Color32>(scriptableRenderContext, m_ResultsBuffer, OnDepthCheckReadback);
        }

        static Vector2Int TextureDimensions(int keypointCount)
        {
            var width = Math.Max(k_MinTextureWidth, Mathf.NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(keypointCount))));
            var height = width;

            var textureDimensions = new Vector2Int(width, height);
            return textureDimensions;
        }

        void OnDepthCheckReadback(int frame, NativeArray<Color32> data, RenderTexture renderTexture)
        {
            DoDepthCheckReadback(frame, data);
        }

        void OnDepthCheckReadback(int frameCount, AsyncGPUReadbackRequest obj)
        {
            var data = obj.GetData<uint>();
//            DoDepthCheckReadback(frameCount, data);
        }

        // Go through each keypoint and check if the depth compute shader has determined if it is visible (depth texture
        // value of 1.
        void DoDepthCheckReadback(int frameCount, NativeArray<Color32> data)
        {
            var frameKeypointData = m_FrameKeypointData[frameCount];
            var totalLength = frameKeypointData.pointsPerEntry * frameKeypointData.keypoints.Count;
            Debug.Assert(totalLength < data.Length);
            for (var i = 0; i < totalLength; i++)
            {
                var value = data[i];
                if (value.r == 0)
                {
                    var keypoints = frameKeypointData.keypoints[i / frameKeypointData.pointsPerEntry];
                    var indexInObject = i % frameKeypointData.pointsPerEntry;
                    var keypoint = keypoints.keypoints[indexInObject];
                    keypoint.state = 1;
                    keypoints.keypoints[indexInObject] = keypoint;
                }
            }

            frameKeypointData.isDepthCheckComplete = true;
            m_FrameKeypointData[frameCount] = frameKeypointData;
            ReportIfComplete(frameCount, frameKeypointData);
        }
        float GetCaptureHeight()
        {
            var targetTexture = perceptionCamera.attachedCamera.targetTexture;
            return targetTexture != null ?
                targetTexture.height : Screen.height;
        }
        Vector3 ConvertToScreenSpace(Vector3 worldLocation)
        {
            var pt = perceptionCamera.attachedCamera.WorldToScreenPoint(worldLocation);
            pt.y = GetCaptureHeight() - pt.y;
            if (Mathf.Approximately(pt.y, perceptionCamera.attachedCamera.pixelHeight))
                pt.y -= .0001f;
            if (Mathf.Approximately(pt.x, perceptionCamera.attachedCamera.pixelWidth))
                pt.x -= .0001f;

            return pt;
        }

        struct CachedData
        {
            public bool status;
            public Animator animator;
            public KeypointComponent keypoints;
            public List<(JointLabel, int)> overrides;
            public float occlusionScalar;
        }

        Dictionary<uint, CachedData> m_KnownStatus;

        bool TryToGetTemplateIndexForJoint(KeypointTemplate template, JointLabel joint, out int index)
        {
            index = -1;

            foreach (var label in joint.labels)
            {
                for (var i = 0; i < template.keypoints.Length; i++)
                {
                    if (template.keypoints[i].label == label)
                    {
                        index = i;
                        return true;
                    }
                }
            }

            return false;
        }

        bool DoesTemplateContainJoint(JointLabel jointLabel)
        {
            return TryToGetTemplateIndexForJoint(activeTemplate, jointLabel, out _);
        }

        void ProcessLabel(Labeling labeledEntity, List<KeypointComponent> keypointEntries, NativeList<float3> checkLocations)
        {
            if (!idLabelConfig.TryGetLabelEntryFromInstanceId(labeledEntity.instanceId, out var labelEntry))
                return;

            // Cache out the data of a labeled game object the first time we see it, this will
            // save performance each frame. Also checks to see if a labeled game object can be annotated.
            if (!m_KnownStatus.ContainsKey(labeledEntity.instanceId))
            {
                var cached = new CachedData()
                {
                    status = false,
                    animator = null,
                    keypoints = new KeypointComponent(),
                    overrides = new List<(JointLabel, int)>(),
                    occlusionScalar = 1.0f
                };

                var entityGameObject = labeledEntity.gameObject;

                cached.keypoints.instanceId = labeledEntity.instanceId;
                cached.keypoints.labelId = labelEntry.id;

                cached.keypoints.keypoints = new KeypointValue[activeTemplate.keypoints.Length];
                for (var i = 0; i < cached.keypoints.keypoints.Length; i++)
                {
                    cached.keypoints.keypoints[i] = new KeypointValue { index = i, state = 0 };
                }

                var animator = entityGameObject.transform.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    cached.animator = animator;
                    cached.status = true;
                }

                foreach (var joint in entityGameObject.transform.GetComponentsInChildren<JointLabel>())
                {
                    if (TryToGetTemplateIndexForJoint(activeTemplate, joint, out var idx))
                    {
                        cached.overrides.Add((joint, idx));
                        cached.status = true;
                    }
                }

                var occlusionOverrider = labeledEntity.GetComponentInParent<KeypointOcclusionOverrides>();
                if (occlusionOverrider != null)
                {
                    cached.occlusionScalar = occlusionOverrider.distanceScale;
                }

                m_KnownStatus[labeledEntity.instanceId] = cached;
            }

            var cachedData = m_KnownStatus[labeledEntity.instanceId];

            if (cachedData.status)
            {
                var animator = cachedData.animator;

                var listStart = checkLocations.Length;
                checkLocations.Resize(checkLocations.Length + activeTemplate.keypoints.Length, NativeArrayOptions.ClearMemory);
                //grab the slice of the list for the current object to assign positions in
                var checkLocationsSlice = new NativeSlice<float3>(checkLocations, listStart);

                var cameraPosition = perceptionCamera.transform.position;
                var cameraforward = perceptionCamera.transform.forward;

                // Go through all of the rig keypoints and get their location
                for (var i = 0; i < activeTemplate.keypoints.Length; i++)
                {
                    var pt = activeTemplate.keypoints[i];
                    if (pt.associateToRig)
                    {
                        var bone = animator.GetBoneTransform(pt.rigLabel);
                        if (bone != null)
                        {
                            var bonePosition = bone.position;

                            var occlusionDistance = pt.selfOcclusionDistance * cachedData.occlusionScalar;
                            var jointSelfOcclusionDistance = JointSelfOcclusionDistance(bone, bonePosition, cameraPosition, cameraforward, occlusionDistance);

                            InitKeypoint(bonePosition, cachedData, checkLocationsSlice, i, jointSelfOcclusionDistance);
                        }
                    }
                }

                // Go through all of the additional or override points defined by joint labels and get
                // their locations
                foreach (var (joint, templateIdx) in cachedData.overrides)
                {
                    var jointTransform = joint.transform;
                    var jointPosition = jointTransform.position;
                    float resolvedSelfOcclusionDistance;
                    if (joint.overrideSelfOcclusionDistance)
                        resolvedSelfOcclusionDistance = joint.selfOcclusionDistance;
                    else
                        resolvedSelfOcclusionDistance = activeTemplate.keypoints[templateIdx].selfOcclusionDistance;

                    resolvedSelfOcclusionDistance *= cachedData.occlusionScalar;

                    var jointSelfOcclusionDistance = JointSelfOcclusionDistance(joint.transform, jointPosition, cameraPosition, cameraforward, resolvedSelfOcclusionDistance);

                    InitKeypoint(jointPosition, cachedData, checkLocationsSlice, templateIdx, jointSelfOcclusionDistance);
                }

                cachedData.keypoints.pose = "unset";

                if (cachedData.animator != null)
                {
                    cachedData.keypoints.pose = GetPose(cachedData.animator);
                }

                var cachedKeypointEntry = cachedData.keypoints;
                var keypointEntry = new KeypointComponent
                {
                    instanceId = cachedKeypointEntry.instanceId,
                    keypoints = DeepCopyKeypoints(cachedKeypointEntry.keypoints),
                    labelId = cachedKeypointEntry.labelId,
                    pose = cachedKeypointEntry.pose,
                };
                keypointEntries.Add(keypointEntry);
            }
        }

        KeypointValue[] DeepCopyKeypoints(IReadOnlyList<KeypointValue> values)
        {
            var copied = new KeypointValue[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copied[i] = values[i].Clone() as KeypointValue;
            }

            return copied;
        }

        float JointSelfOcclusionDistance(Transform transform, Vector3 jointPosition, Vector3 cameraPosition,
            Vector3 cameraforward, float configuredSelfOcclusionDistance)
        {
            var depthOfJoint = Vector3.Dot(jointPosition - cameraPosition, cameraforward);
            var cameraEffectivePosition = jointPosition - cameraforward * depthOfJoint;

            var jointRelativeCameraPosition = transform.InverseTransformPoint(cameraEffectivePosition);
            var jointRelativeCheckPosition = jointRelativeCameraPosition.normalized * configuredSelfOcclusionDistance;
            var worldSpaceCheckVector = transform.TransformVector(jointRelativeCheckPosition);
            return worldSpaceCheckVector.magnitude;
        }

        void InitKeypoint(Vector3 position, CachedData cachedData, NativeSlice<float3> checkLocations, int idx,
            float occlusionDistance)
        {
            var loc = ConvertToScreenSpace(position);

            var keypoints = cachedData.keypoints.keypoints;
            keypoints[idx].index = idx;
            if (loc.z < 0)
            {
                keypoints[idx].location = Vector2.zero;
                keypoints[idx].state = 0;
            }
            else
            {
                keypoints[idx].location = new Vector2(loc.x, loc.y);
                keypoints[idx].state = 2;
            }

            //TODO: move this code
            var pixelLocation = PixelLocationFromScreenPoint(keypoints[idx]);
            if (pixelLocation.x < 0 || pixelLocation.y < 0)
            {
                pixelLocation = new int2(int.MaxValue, int.MaxValue);
            }

            checkLocations[idx] = new float3(pixelLocation.x + .5f, pixelLocation.y + .5f, loc.z - occlusionDistance);
        }

        string GetPose(Animator animator)
        {
            var info = animator.GetCurrentAnimatorClipInfo(0);

            if (info != null && info.Length > 0)
            {
                var clip = info[0].clip;
                var timeOffset = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;

                if (animationPoseConfigs != null)
                {
                    foreach (var p in animationPoseConfigs)
                    {
                        if (p != null && p.animationClip == clip)
                        {
                            var time = timeOffset;
                            var label = p.GetPoseAtTime(time);
                            return label;
                        }
                    }
                }
            }

            return "unset";
        }

        KeypointValue GetKeypointForJoint(KeypointComponent entry, int joint)
        {
            if (joint < 0 || joint >= entry.keypoints.Length) return null;
            return entry.keypoints[joint];
        }

        /// <inheritdoc/>
        protected override void OnVisualize()
        {
            if (m_LatestReported == null) return;
            using (s_OnVisualizeMarker.Auto())
            {
                var jointTexture = activeTemplate.jointTexture;
                if (jointTexture == null) jointTexture = m_MissingTexture;

                var skeletonTexture = activeTemplate.skeletonTexture;
                if (skeletonTexture == null) skeletonTexture = m_MissingTexture;

                foreach (var entry in m_LatestReported)
                {
                    foreach (var bone in activeTemplate.skeleton)
                    {
                        var joint1 = GetKeypointForJoint(entry, bone.joint1);
                        var joint2 = GetKeypointForJoint(entry, bone.joint2);

                        if (joint1 != null && joint1.state == 2 && joint2 != null && joint2.state == 2)
                        {
                            VisualizationHelper.DrawLine(joint1.location.x, joint1.location.y, joint2.location.x, joint2.location.y, bone.color, 8, skeletonTexture);
                        }
                    }

                    foreach (var keypoint in entry.keypoints)
                    {
                        if (keypoint.state == 2)
                            VisualizationHelper.DrawPoint(keypoint.location.x, keypoint.location.y, activeTemplate.keypoints[keypoint.index].color, 8, jointTexture);
                    }
                }
            }
        }

        // TODO rename this method
        KeypointAnnotationDefinition.Template TemplateToJson(KeypointTemplate input, IdLabelConfig labelConfig)
        {
            var json = new KeypointAnnotationDefinition.Template
            {
                templateId = input.templateID,
                templateName = input.templateName,
                keyPoints = new KeypointAnnotationDefinition.JointDefinition[input.keypoints.Length],
                skeleton = new KeypointAnnotationDefinition.SkeletonDefinition[input.skeleton.Length]
            };

            for (var i = 0; i < input.keypoints.Length; i++)
            {
                json.keyPoints[i] = new KeypointAnnotationDefinition.JointDefinition
                {
                    label = input.keypoints[i].label,
                    index = i,
                    color = input.keypoints[i].color
                };
            }

            for (var i = 0; i < input.skeleton.Length; i++)
            {
                json.skeleton[i] = new KeypointAnnotationDefinition.SkeletonDefinition
                {
                    joint1 = input.skeleton[i].joint1,
                    joint2 = input.skeleton[i].joint2,
                    color = input.skeleton[i].color
                };
            }

            return json;
        }
    }
}
