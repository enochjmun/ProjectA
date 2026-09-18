using Unity.Netcode;
using UnityEngine;

/// <summary>
/// On the Player. Moves ONLY this client's own model to the "LocalPlayer" layer at
/// runtime, so the owner camera (whose culling mask excludes LocalPlayer) hides just
/// your own body. The prefab keeps the model on a visible layer (Default), so remote
/// copies on other clients still render.
///
/// Ownership isn't known until the object spawns, so this can't be baked into the
/// prefab -- it must be an IsOwner-gated runtime step. Rendering is per-client: on
/// your machine only YOUR player is the owner, so only your model is hidden; every
/// remote player stays on the visible layer and renders normally.
/// </summary>
public class LocalPlayerModelHider : NetworkBehaviour
{
    [Tooltip("Root of the character mesh. Its whole hierarchy is moved to the " +
             "LocalPlayer layer for the owner (child bones/submeshes included).")]
    [SerializeField] private GameObject modelRoot;

    [Tooltip("Layer excluded by the owner camera's culling mask. Must exist in the " +
             "Tags & Layers list.")]
    [SerializeField] private string hiddenLayerName = "LocalPlayer";

    public override void OnNetworkSpawn()
    {
        if (!IsOwner || modelRoot == null)
            return;

        int layer = LayerMask.NameToLayer(hiddenLayerName);
        if (layer < 0)
        {
            Debug.LogWarning($"[LocalPlayerModelHider] No '{hiddenLayerName}' layer exists -- add it in Tags & Layers.", this);
            return;
        }

        SetLayerRecursive(modelRoot, layer);
    }

    // Layer changes don't cascade to children automatically, so walk the whole mesh
    // hierarchy -- otherwise child bones/submeshes stay on the visible layer.
    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursive(child.gameObject, layer);
    }
}
