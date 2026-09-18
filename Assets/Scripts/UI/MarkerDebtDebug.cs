using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// DEV-ONLY: test the persistent debt without a full match. Drop on any GameObject in the menu scene.
/// Keys:
///   P  service a small payment (default 12 — the pittance)   → balance ticks down, persists
///   L  service a larger payment (default 750)                → crosses reward milestones
///   K  reset the marker to the starting debt (ResetForTesting)
/// Watch the marker's amount change, then STOP and re-enter Play — it should come back lower (PlayerPrefs).
/// Remove/disable before shipping.
/// </summary>
public class MarkerDebtDebug : MonoBehaviour
{
    [SerializeField] private long smallPayment = 12;
    [SerializeField] private long largePayment = 750;

    private void Update()
    {
        if (Down(Key.P, KeyCode.P))
            Debug.Log($"[Debt] serviced {smallPayment} → balance {PlayerDebt.Balance}, serviced total {PlayerDebt.Serviced}, milestones +{PlayerDebt.Service(smallPayment)}");
        if (Down(Key.L, KeyCode.L))
            Debug.Log($"[Debt] serviced {largePayment} → balance {PlayerDebt.Balance}, serviced total {PlayerDebt.Serviced}, milestones +{PlayerDebt.Service(largePayment)}");
        if (Down(Key.K, KeyCode.K))
        {
            PlayerDebt.ResetForTesting();
            Debug.Log($"[Debt] reset → balance {PlayerDebt.Balance}");
        }
    }

    // NOTE on the Service log above: PlayerDebt.Service runs inside the string, so the "balance" printed is
    // the value BEFORE this payment; the marker itself shows the updated value after.
    private static bool Down(Key newKey, KeyCode oldKey)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null) return Keyboard.current[newKey].wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(oldKey);
#else
        return false;
#endif
    }
}
