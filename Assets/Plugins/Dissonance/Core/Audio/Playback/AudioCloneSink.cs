using System;
using UnityEngine;

namespace Dissonance.Audio.Playback
{
    /// <summary>
    /// Clones audio from a voice audio source to another audio source.
    /// This script must be attached next to an AudioSource.
    /// See also <see cref="AudioCloneSource"/>.
    /// </summary>
    public class AudioCloneSink
        : MonoBehaviour
    {
        #region fields and properties
        private static readonly Log Log = Logs.Create(LogCategory.Playback, "Audio Clone Sink");

        private AudioSource _audioSource;

        /// <summary>
        /// Whether the AudioSource should automatically start playing when this behaviour is enabled.
        /// </summary>
        public bool AutoPlay = true;

        /// <summary>
        /// The <see cref="AudioCloneSource"/> to clone audio from.
        /// </summary>
        public AudioCloneSource Source;
        private AudioCloneSource _subscribedTo;

        private readonly float[] _audioBuffer = new float[48000];
        private bool _complete = true;
        #endregion

        private void Awake()
        {
            if (!_audioSource)
                _audioSource = GetComponent<AudioSource>();
        }

        private void OnEnable()
        {
            if (!_audioSource)
            {
                Log.Error("AudioSource component is missing. AudioCloneSink requires an AudioSource on the same GameObject.");
                return;
            }
            _audioSource.Stop();

            // There is no low-latency way to play back audio into a spatialized AudioSource. Disable spatialization for this source if it's enabled.
            if (_audioSource.spatialize)
            {
                Log.Debug("spatialized AudioSource not supported for voice playback. Setting `spatialize=false`.");
                _audioSource.spatialize = false;
            }

            // Play back a flatline of 1.0 through the source and then multiply the voice signal by that to achieve spatial blending of voice.
            _audioSource.clip = AudioClip.Create("Flatline", 4096, 1, AudioSettings.outputSampleRate, false, buf =>
            {
                for (var i = 0; i < buf.Length; i++)
                    buf[i] = 1.0f;
            });

            // Set all of the audio source settings that are not allowed to be changed
            _audioSource.loop = true;        // Audio must play forever
            _audioSource.pitch = 1;          // Pitch has no effect on the audio
            _audioSource.dopplerLevel = 0;   // Pitch cannot be changed, so doppler makes no sense

            if (AutoPlay)
                _audioSource.Play();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (_subscribedTo != Source)
                Subscribe(Source);
        }

        private void Subscribe(AudioCloneSource source)
        {
            Unsubscribe();

            if (source)
            {
                source.Subscribe(this);
                _subscribedTo = source;
            }
        }

        private void Unsubscribe()
        {
            if (_subscribedTo)
            {
                _subscribedTo.Unsubscribe(this);
                _subscribedTo = null;
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_complete)
            {
                Array.Clear(data, 0, data.Length);
            }
            else
            {
                SamplePlaybackComponent.StretchAudioChannels(data, channels, _audioBuffer);
            }
        }

        internal void OnAudioPlayback(ArraySegment<float> data, bool complete)
        {
            // Sanity check buffer size. This shouldn't ever happen since we've reserved an enormous buffer (48000 samples) for
            // a tiny amount of audio (1 frame, probably 10ms or 480 sample)
            if (data.Count > _audioBuffer.Length)
            {
                Log.Error($"Audio data too large: {data.Count} > {_audioBuffer.Length}");
                Array.Clear(_audioBuffer, 0, _audioBuffer.Length);
                return;
            }

            data.CopyTo(_audioBuffer);
            _complete = complete;
        }
    }
}
