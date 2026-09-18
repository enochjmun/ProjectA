using Dissonance;
using Dissonance.Integrations.Unity_NFGO;
using UnityEngine;

/// <summary>
/// Bridges each player's live Dissonance voice amplitude into their FaceController, so the mouth
/// lip-syncs while they talk.
///
/// WHY NO NETWORKING: Dissonance decodes every speaker's playback amplitude LOCALLY on each client
/// (VoicePlayerState.Amplitude — mic level for the local player, decoded playback level for remotes).
/// So this runs on EVERY client for EVERY player and reads the amplitude that's already present on
/// that machine — no NetworkVariable, no RPC, zero bandwidth for the mouth. (Same philosophy as the
/// VoiceSpatialProcessor occlusion pass: voice reactions are local because the audio is local.)
///
/// The local player's own model is hidden (first-person), so driving their own mouth is harmless;
/// what matters is that REMOTE avatars' mouths move, which they do because their voice is playing
/// right here.
///
/// IDENTITY: the Dissonance player name lives on NfgoPlayer.PlayerId (set once the player's name
/// replicates). FindPlayer(id) resolves that to the VoicePlayerState carrying the amplitude.
///
/// SETUP: put on the Player prefab (same object as NfgoPlayer). Assign the FaceController (auto-found
/// in children if left empty). Tune `gain` so a normal speaking voice opens the mouth well.
/// </summary>
[RequireComponent(typeof(NfgoPlayer))]
public class FaceVoiceDriver : MonoBehaviour
{
    [Tooltip("The face to drive. Auto-found in children if left empty.")]
    [SerializeField] private FaceController face;
    [Tooltip("Multiplies Dissonance's raw amplitude (a fairly small RMS level) up to a usable 0..1 " +
             "mouth-open. Raise if mouths barely move while people talk; lower if they gape at a whisper.")]
    [SerializeField] private float gain = 6f;
    [Tooltip("Minimum mouth-open while the player IS speaking, no matter how quiet. Guarantees a soft " +
             "talker's mouth still visibly moves instead of staying shut. Louder speech opens further. " +
             "0 = purely amplitude-driven (a very quiet voice may barely move).")]
    [Range(0f, 1f)] [SerializeField] private float minOpenWhileSpeaking = 0.12f;

    private NfgoPlayer _player;
    private DissonanceComms _comms;
    private VoicePlayerState _state;
    private string _resolvedId;

    private void Awake()
    {
        _player = GetComponent<NfgoPlayer>();
        if (face == null) face = GetComponentInChildren<FaceController>();
    }

    private void Update()
    {
        if (face == null) return;
        if (_comms == null) _comms = DissonanceComms.GetSingleton();
        if (_comms == null) return;

        // The Dissonance name isn't set until the player's id NetworkVariable replicates; until then
        // (and if the player leaves) hold the mouth closed.
        string id = _player.PlayerId;
        if (string.IsNullOrEmpty(id)) { face.SetTalkAmplitude(0f); return; }

        // Resolve the amplitude source; re-resolve if it's not found yet, its id changed, or the
        // player dropped and its state went stale (a disconnected remote reports Amplitude 0 anyway,
        // but re-resolving keeps us pointed at a live handle if they reconnect).
        if (_state == null || _resolvedId != id || !_state.IsConnected)
        {
            _state = _comms.FindPlayer(id);
            _resolvedId = id;
        }
        if (_state == null) { face.SetTalkAmplitude(0f); return; }

        // Gate on IsSpeaking (Dissonance's voice-activity/transmission flag), NOT on an amplitude
        // threshold — that's what separates "silent" from "speaking quietly", so a soft talker's mouth
        // still moves. When speaking, openness comes from amplitude (how loud), with a floor so even a
        // very quiet voice is visibly open. IsSpeaking already excludes idle hiss (it's post-VAD/PTT).
        float amp = 0f;
        if (_state.IsSpeaking)
            amp = Mathf.Max(minOpenWhileSpeaking, _state.Amplitude * gain);
        face.SetTalkAmplitude(amp);   // FaceController clamps to 0..1 and eases it onto the Open shape
    }
}
