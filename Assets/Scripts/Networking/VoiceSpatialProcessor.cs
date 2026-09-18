using UnityEngine;

namespace CasinoHorrorGame.Networking
{
    /// <summary>
    /// Environmental voice processing for Dissonance positional playback (audio pass, step 2).
    /// Two effects, both LOCAL (no netcode) — same shape as the trapdoor / lighting reactors:
    ///   1. OCCLUSION — barrier between speaker and listener muffles (cutoff down) and quietens
    ///      (volume down) by how much MASS is in the way.
    ///   2. DISTANCE — far voices lose highs to air absorption (cutoff down only).
    ///
    /// WHERE IT LIVES: on the Dissonance playback prefab (Prefabs/Test Voice.prefab). That GameObject
    /// IS the speaker — Dissonance moves each spawned copy to the talking player's position for
    /// positional playback — so `transform.position` is the mouth and the local AudioListener (on the
    /// Player prefab) is the ear.
    ///
    /// THE PHYSICS: sound through a barrier isn't uniformly quieter, it's FILTERED. High frequencies are
    /// short-wavelength / low-energy so mass absorbs them; low frequencies are long-wavelength and
    /// DIFFRACT — they bend around edges and pass through material. So "muffled" == "low-pass cutoff
    /// drops", and it NEVER fully mutes (the lows always get through — that's the volume FLOOR below,
    /// not a bug). The same absorption over distance is why a far voice loses its highs even in open air.
    ///
    /// DIVISION OF LABOUR with Dissonance/Unity: the AudioSource's own rolloff (MinDistance/MaxDistance)
    /// already fades LOUDNESS over distance, so this script does NOT duck volume by distance — only
    /// cutoff. Unity has no concept of occlusion at all, so occlusion drives BOTH cutoff and volume here.
    /// Dissonance applies its per-speaker gain in the DSP pipeline (an IVolumeProvider), never on
    /// AudioSource.volume, so writing AudioSource.volume here is safe and composes multiplicatively.
    ///
    /// It DRIVES the prefab's existing baseline AudioLowPassFilter (from the voice-dirtying pass),
    /// capturing that filter's tuned cutoff at startup as the "open voice" value and only ever pulling
    /// DOWN from there — so an un-occluded, close voice sits exactly where you tuned it.
    /// </summary>
    [RequireComponent(typeof(AudioLowPassFilter))]
    [RequireComponent(typeof(AudioSource))]
    public class VoiceSpatialProcessor : MonoBehaviour
    {
        [Header("What blocks voice")]
        [Tooltip("Walls / floors / solid environment (Environment + Dungeon + HoleEraser). MUST exclude " +
                 "players, triggers, and the voice playback objects themselves, or a speaker occludes itself.")]
        [SerializeField] private LayerMask occluderMask = 0;

        [Header("Occlusion cutoffs (Hz)")]
        [Tooltip("Open-voice cutoff. -1 = capture whatever the baseline low-pass is tuned to at startup " +
                 "(recommended, so this never fights your dirtying pass).")]
        [SerializeField] private float openCutoff = -1f;
        [Tooltip("Cutoff through a THIN barrier (stud wall / door). Muffled but some consonants survive. ~1.5 kHz.")]
        [SerializeField] private float thinWallCutoff = 1500f;
        [Tooltip("Cutoff through a THICK barrier (trapdoor floor / concrete slab). Pure bass mumble. ~500–800 Hz.")]
        [SerializeField] private float thickFloorCutoff = 650f;

        [Header("Thickness → occlusion mapping (metres)")]
        [Tooltip("Barrier this thin (or thinner) = thinWallCutoff and almost no volume duck.")]
        [SerializeField] private float thinThickness = 0.3f;
        [Tooltip("Barrier this thick (or thicker) = thickFloorCutoff and the full volume duck. Lerps between.")]
        [SerializeField] private float thickThickness = 2f;

        [Header("Occlusion volume duck")]
        [Tooltip("Quietest an occluded voice gets, as a fraction of its normal volume, at max thickness. " +
                 "NEVER 0 — the lows diffract through, so a walled-off voice is a muffled murmur, not " +
                 "silence. A THIN wall barely ducks (mostly a tonal change); only MASS kills loudness.")]
        [Range(0f, 1f)] [SerializeField] private float duckFloor = 0.4f;

        [Header("Distance rolloff (cutoff only — Unity's rolloff already handles distance volume)")]
        [Tooltip("At/under this distance (m), no air muffle — voice stays at its open cutoff.")]
        [SerializeField] private float distanceNear = 6f;
        [Tooltip("At/over this distance (m), the voice sits at distanceFarCutoff.")]
        [SerializeField] private float distanceFar = 45f;
        [Tooltip("Cutoff for a far voice in open air. Air is a GENTLE low-pass — keep this well above the " +
                 "occlusion targets (~2.5–3 kHz), so a far-but-clear voice only softly darkens.")]
        [SerializeField] private float distanceFarCutoff = 2800f;

        [Header("Feel")]
        [Tooltip("How fast cutoff & volume ease toward target. Higher = snappier; smoothing stops the " +
                 "filter zippering as people move.")]
        [SerializeField] private float smoothingSpeed = 8f;
        [Tooltip("Seconds between raycasts. The RAYCAST is throttled (geometry doesn't change between " +
                 "frames); cutoff & volume still ease EVERY frame, so audio stays smooth.")]
        [SerializeField] private float raycastInterval = 0.05f;

        private AudioLowPassFilter _lowPass;
        private AudioSource _source;
        private Transform _listener;      // active AudioListener transform (local ear)
        private float _openCutoff;        // resolved open-voice cutoff
        private float _baseVolume;        // authored AudioSource volume (the "un-ducked" level)
        private float _targetCutoff, _currentCutoff;
        private float _targetVolume, _currentVolume;
        private float _tick;

        private void Awake()
        {
            _lowPass = GetComponent<AudioLowPassFilter>();
            _source = GetComponent<AudioSource>();
        }

        private void OnEnable()
        {
            // Read here (not Awake): this playback object is pooled/reused by Dissonance, so re-resolve
            // the tuned baselines each time it's brought back to life.
            _openCutoff = openCutoff > 0f ? openCutoff : _lowPass.cutoffFrequency;
            _baseVolume = _source.volume;
            _targetCutoff = _currentCutoff = _openCutoff;
            _targetVolume = _currentVolume = _baseVolume;
            _lowPass.enabled = true;

            // Voice must pick up AudioReverbZones (bar/pit — see DungeonReverbZone). Dissonance forces
            // bypassReverbZones = true on AudioSources it AUTO-creates; enforce false here so a pooled or
            // re-created playback source can never silently drop out of the reverb zones.
            _source.bypassReverbZones = false;

            _tick = 0f;
        }

        private void Update()
        {
            // Throttled sensing, per-frame easing (so the effect glides instead of stepping).
            _tick -= Time.deltaTime;
            if (_tick <= 0f)
            {
                _tick = raycastInterval;
                Recompute();
            }

            // Framerate-independent exponential ease (same 1 - e^(-k·dt) as AreaEnvironment / lighting).
            float t = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
            _currentCutoff = Mathf.Lerp(_currentCutoff, _targetCutoff, t);
            _currentVolume = Mathf.Lerp(_currentVolume, _targetVolume, t);
            _lowPass.cutoffFrequency = _currentCutoff;
            _source.volume = _currentVolume;
        }

        /// <summary>
        /// Sets both targets. Cutoff = min(occlusion, distance) — whichever says "darker" wins, so the
        /// two never double-count. Volume ducks by OCCLUSION thickness only.
        /// </summary>
        private void Recompute()
        {
            Transform ear = Listener();
            if (ear == null)
            {
                _targetCutoff = _openCutoff;
                _targetVolume = _baseVolume;
                return;
            }

            Vector3 speaker = transform.position;
            Vector3 listener = ear.position;
            float distance = Vector3.Distance(speaker, listener);

            // --- Occlusion: linecast both ways to bracket the barrier's thickness ---
            float occlusionCutoff = _openCutoff;
            float duck = 1f;   // 1 = full volume (no occluder)
            if (Physics.Linecast(speaker, listener, out RaycastHit entry, occluderMask,
                                 QueryTriggerInteraction.Ignore))
            {
                // entry.point = barrier's near face on the SPEAKER side; the reverse cast gives its near
                // face on the LISTENER side, so the gap between them is the mass crossed. (Two separate
                // walls read as the outer-face gap — an over-estimate, i.e. "extra muffled", a reasonable
                // failure.) A missed reverse cast falls back to thin.
                float thickness = thinThickness;
                if (Physics.Linecast(listener, speaker, out RaycastHit exit, occluderMask,
                                     QueryTriggerInteraction.Ignore))
                    thickness = Vector3.Distance(entry.point, exit.point);

                float k = Mathf.InverseLerp(thinThickness, thickThickness, thickness);   // 0 thin → 1 thick
                occlusionCutoff = Mathf.Lerp(thinWallCutoff, thickFloorCutoff, k);
                // Volume: thin wall ≈ untouched (tonal change only); mass drives it down to the floor.
                duck = Mathf.Lerp(1f, duckFloor, k);
            }

            // --- Distance: gentle air low-pass, cutoff only (Unity's rolloff owns distance loudness) ---
            float dk = Mathf.InverseLerp(distanceNear, distanceFar, distance);   // 0 near → 1 far
            float distanceCutoff = Mathf.Lerp(_openCutoff, distanceFarCutoff, dk);

            // Darker of the two wins; never above the tuned open value.
            _targetCutoff = Mathf.Min(Mathf.Min(occlusionCutoff, distanceCutoff), _openCutoff);
            _targetVolume = _baseVolume * duck;
        }

        /// <summary>
        /// The active local ear. Cached; re-found if it goes null (the listener can move — e.g. a
        /// spectator's view). FindObjectOfType only runs when the cache is empty, not per ray.
        /// </summary>
        private Transform Listener()
        {
            if (_listener != null)
                return _listener;

            AudioListener al = FindObjectOfType<AudioListener>();
            _listener = al != null ? al.transform : null;
            return _listener;
        }

#if UNITY_EDITOR
        // Scene-view aid: speaker→listener line, red when occluded, green when clear.
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || _listener == null)
                return;
            bool blocked = Physics.Linecast(transform.position, _listener.position, occluderMask,
                                            QueryTriggerInteraction.Ignore);
            Gizmos.color = blocked ? Color.red : Color.green;
            Gizmos.DrawLine(transform.position, _listener.position);
        }
#endif
    }
}
