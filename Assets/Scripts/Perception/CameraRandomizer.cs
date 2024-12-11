using System;
using UnityEngine;
using UnityEngine.Perception.Randomization.Randomizers;
using UnityEngine.Perception.Randomization.Parameters;

[Serializable]
[AddRandomizerMenu("Perception/Camera Randomizer")]
public class CameraRandomizer : Randomizer
{
    public Camera mainCamera;
    public FloatParameter cameraXRotation;  // Elevation (pitch) in degrees
    public FloatParameter cameraYRotation;  // Yaw (horizontal rotation) in degrees
    public FloatParameter cameraDistance;   // Distance from the original point

    private Vector3 initialPosition;
    private Quaternion initialRotation;

    protected override void OnAwake()
    {
        // Store the camera's initial position and rotation
        initialPosition = mainCamera.transform.position;
        initialRotation = mainCamera.transform.rotation;
    }

    protected override void OnIterationStart()
    {
        // Sample random pitch, yaw, and distance
        var pitch = cameraXRotation.Sample(); // Elevation (pitch)
        var yaw = cameraYRotation.Sample();   // Yaw (horizontal rotation)
        var distance = cameraDistance.Sample();

        // Calculate the new position using spherical coordinates
        float pitchRad = pitch * Mathf.Deg2Rad;
        float yawRad = yaw * Mathf.Deg2Rad;

        Vector3 offset = new Vector3(
            distance * Mathf.Sin(yawRad) * Mathf.Cos(pitchRad), // X
            distance * Mathf.Sin(pitchRad),                    // Y
            distance * Mathf.Cos(yawRad) * Mathf.Cos(pitchRad) // Z
        );

        // Set the camera's new position
        mainCamera.transform.position = initialPosition + offset;

        // Rotate the camera to look back at the initial position
        mainCamera.transform.rotation = Quaternion.LookRotation(initialPosition - mainCamera.transform.position);
    }
}
