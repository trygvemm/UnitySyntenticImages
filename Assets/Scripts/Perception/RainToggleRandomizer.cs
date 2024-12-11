using System;
using UnityEngine;
using UnityEngine.Perception.Randomization.Randomizers;

[Serializable]
[AddRandomizerMenu("Perception/Rain Toggle Randomizer")]
public class RainToggleRandomizer : Randomizer
{
    public GameObject rainPrefab; // Reference to the RainPrefab
    public int totalImages = 10;  // The total number of iterations (images) before deactivating rain

    private int currentIteration = 0; // Tracks the current iteration

    protected override void OnIterationStart()
    {
        // Check if the current iteration is less than the total number of images
        if (currentIteration < totalImages)
        {
            // Activate the rainPrefab (100% probability)
            rainPrefab.SetActive(true);
            Debug.Log($"Iteration {currentIteration + 1}/{totalImages}: Rain is enabled.");
        }
        else
        {
            // After the specified number of images, deactivate the rainPrefab
            rainPrefab.SetActive(false);
            Debug.Log("Rain is disabled.");
        }

        // Increment the iteration counter
        currentIteration++;
    }
}
