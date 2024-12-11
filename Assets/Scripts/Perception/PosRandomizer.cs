using System;
using UnityEngine;
using UnityEngine.Perception.Randomization.Randomizers;
using UnityEngine.Perception.Randomization.Parameters;

[Serializable]
[AddRandomizerMenu("Perception/Object Position Randomizer")]
public class ObjectPositionRandomizer : Randomizer
{
    public GameObject targetObject; // The object to randomize
    public FloatParameter offsetX;  // X-axis offset
    public FloatParameter offsetY;  // Y-axis offset
    public FloatParameter offsetZ;  // Z-axis offset

    private Vector3 initialPosition;

    protected override void OnAwake()
    {
        // Store the object's initial position
        if (targetObject != null)
        {
            initialPosition = targetObject.transform.position;
        }
        else
        {
            Debug.LogError("Target object is not assigned.");
        }
    }

    protected override void OnIterationStart()
    {
        if (targetObject == null) return;

        // Sample random offsets for X, Y, and Z directions
        float randomX = offsetX.Sample();
        float randomY = offsetY.Sample();
        float randomZ = offsetZ.Sample();

        // Apply the random offsets to the initial position
        Vector3 newPosition = initialPosition + new Vector3(randomX, randomY, randomZ);

        // Update the target object's position
        targetObject.transform.position = newPosition;
    }
}
