# The Perception Camera Component
The Perception Camera component ensures that the [Camera](https://docs.unity3d.com/Manual/class-Camera.html) runs at deterministic rates. It also ensures that the Camera uses [DatasetCapture](DatasetCapture.md) to capture RGB and other Camera-related ground truth in the [JSON dataset](Schema/Synthetic_Dataset_Schema.md). You can use the Perception Camera component on the High Definition Render Pipeline (HDRP) or the Universal Render Pipeline (URP).

<p align="center">
<img src="images/PerceptionCameraFinished.png" width="600"/>
  <br><i>The Inspector view of the Perception Camera component</i>
</p>

## Properties
| Property: | Function: |
|--|--|
| Description | A description of the Camera to be registered in the JSON dataset. |
| Show Visualizations | Display realtime visualizations for labelers that are currently active on this camera. |
| Capture RGB Images | When you enable this property, Unity captures RGB images as PNG files in the dataset each frame. |
| Capture Trigger Mode | The method of triggering captures for this camera. In `Scheduled` mode, captures happen automatically based on a start frame and frame delta time. In `Manual` mode, captures should be triggered manually through calling the `RequestCapture` method of `PerceptionCamera`. |
| Camera Labelers | A list of labelers that generate data derived from this Camera. |

### Properties for Scheduled Capture Mode
| Property: | Function: |
|--|--|
| Simulation Delta Time | The simulation frame time (seconds) for this camera. E.g. 0.0166 translates to 60 frames per second. This will be used as Unity's `Time.captureDeltaTime`, causing a fixed number of frames to be generated for each second of elapsed simulation time regardless of the capabilities of the underlying hardware. For more information on sensor scheduling, see [DatasetCapture](DatasetCapture.md). |
| First Capture Frame | Frame number at which this camera starts capturing. |
| Frames Between Captures | The number of frames to simulate and render between the camera's scheduled captures. Setting this to 0 makes the camera capture every frame. |

### Properties for Manual Capture Mode
| Property: | Function: |
|--|--|
| Affect Simulation Timing | Have this camera affect simulation timings (similar to a scheduled camera) by requesting a specific frame delta time. Enabling this option will let you set the `Simulation Delta Time` property described above.|

## Output Resolution
When using Unity Editor to generate datasets, the resolution of the images generated by the Perception Camera will match the resolution set for the ***Game*** view of the editor. However, images generated with built players (including local builds and Unity Simulation runs) will use the resolution specified in project settings. 

* To set the resolution of the ***Game*** view, click on the dropdown menu in front of `Display 1`. You can use one of the provided resolutions or create a new one. To create one, click **+**. Set `Type` to `Fixed Resolution` and `Width` and `Height` to your desired resolution.

<p align="center">
<img src="images/gameview_res.png" width="300"/>
  <br><i>Creating a new resolution preset for the ***Game*** view</i>
</p>

* To set the resolution of the built player, Open ***Edit -> Project Settings*** and navigate to the ***Player*** tab. In the ***Resolution and Presentation*** section, set ***Fullscreen Mode*** to ***Windowed*** and then set ***Default Screen Width*** and ***Default Screen Height*** to your desired resolution.

<p align="center">
<img src="images/build_res.png" width="700"/>
  <br><i>Setting the resolution of the built player</i>
</p>

## Camera Labelers
Camera labelers capture data related to the Camera in the JSON dataset. You can use this data to train models and for dataset statistics. The Perception package provides several Camera labelers, and you can derive from the CameraLabeler class to define more labelers.

### Semantic Segmentation Labeler
![Example semantic segmentation image from a modified SynthDet project](images/semantic_segmentation.png)
<br/>_Example semantic segmentation image from a modified SynthDet project_

The SemanticSegmentationLabeler generates a 2D RGB image with the attached Camera. Unity draws objects in the color you associate with the label in the SemanticSegmentationLabelingConfiguration. If Unity can't find a label for an object, it draws it in black.

### Instance Segmentation Labeler

The instance segmentation labeler generates a 2D RGB image with the attached camera. Unity draws each instance of a labeled 
object with a unique color.

### Bounding Box 2D Labeler
![Example bounding box visualization from SynthDet generated by the `SynthDet_Statistics` Jupyter notebook](images/bounding_boxes.png)
<br/>_Example bounding box visualization from SynthDet generated by the `SynthDet_Statistics` Jupyter notebook_

The BoundingBox2DLabeler produces 2D bounding boxes for each visible object with a label you define in the IdLabelConfig.  Unity calculates bounding boxes using the rendered image, so it only excludes occluded or out-of-frame portions of the objects.

### Bounding Box 3D Labeler

The Bounding Box 3D Ground Truth Labeler produces 3D ground truth bounding boxes for each labeled game object in the scene. Unlike the 2D bounding boxes, 3D bounding boxes are calculated from the labeled meshes in the scene and all objects (independent of their occlusion state) are recorded.  
***Note:*** The Bounding Box 3D Labeler does not support GameObjects with Skinned Mesh Renderers, they will be ignored  

### Object Count Labeler

```
{
    "label_id": 25,
    "label_name": "drink_whippingcream_lucerne",
    "count": 1
}
```
_Example object count for a single label_

The ObjectCountLabeler records object counts for each label you define in the IdLabelConfig. Unity only records objects that have at least one visible pixel in the Camera frame.

### Rendered Object Info Labeler
```
{
    "label_id": 24,
    "instance_id": 320,
    "visible_pixels": 28957
}
```
_Example rendered object info for a single object_

The `RenderedObjectInfoLabeler` records a list of all objects visible in the camera image, including their instance IDs, resolved label IDs, and visible pixel counts. If Unity cannot resolve objects to a label in the `IdLabelConfig`, it does not record these objects.

### Keypoint Labeler

The keypoint labeler captures the screen locations of specific points on labeled GameObjects. The typical use of this Labeler is capturing human pose estimation data, but it can be used to capture points on any kind of object. The Labeler uses a [Keypoint Template](#KeypointTemplate) which defines the keypoints to capture for the model and the skeletal connections between those keypoints. The positions of the keypoints are recorded in pixel coordinates.

For more information, see [Keypoint Labeler](GroundTruth/KeypointLabeler.md) or the [Human Pose Labeling and Randomization Tutorial](HPTutorial/TUTORIAL.md)

## Limitations

Ground truth is not compatible with all rendering features, especially those that modify the visibility or shape of objects in the frame.

When generating ground truth:
* Unity does not run Vertex and geometry shaders
* Unity does not consider transparency and considers all geometry opaque 
* Unity does not run post-processing effects, except built-in lens distortion in URP and HDRP

If you discover more incompatibilities, please open an issue in the [Perception GitHub repository](https://github.com/Unity-Technologies/com.unity.perception/issues). 
