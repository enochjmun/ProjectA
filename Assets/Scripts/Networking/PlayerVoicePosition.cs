using UnityEngine;

public class PlaybackPositionProbe : MonoBehaviour
{
    void OnEnable()
    {
        // Logs where this playback object sits the moment Dissonance activates it for a speaker
        Debug.Log($"[PlaybackProbe] spawned at {transform.position}");
    }

    void Start()
    {
        var src = GetComponent<AudioSource>();
        if (src != null)
            Debug.Log($"[PlaybackProbe] spatialBlend={src.spatialBlend} (1=3D, 0=2D), maxDist={src.maxDistance}");
    }
}   