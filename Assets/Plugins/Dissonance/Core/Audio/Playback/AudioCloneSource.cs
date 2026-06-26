using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dissonance.Audio.Playback
{
    /// <summary>
    /// Captures audio from a voice audio source, so that it can be cloned to another AudioSource.
    /// This script must be attached to the playback prefab.
    /// See also <see cref="AudioCloneSink"/>.
    /// </summary>
    public class AudioCloneSource
        : MonoBehaviour, IAudioOutputSubscriber
    {
        #region fields and properties
        private readonly List<AudioCloneSink> _sinks = new();
        #endregion

        void IAudioOutputSubscriber.OnAudioPlayback(ArraySegment<float> data, bool complete)
        {
            lock (_sinks)
                foreach (var sink in _sinks)
                    sink.OnAudioPlayback(data, complete);
        }

        public void Subscribe(AudioCloneSink sink)
        {
            lock (_sinks)
                _sinks.Add(sink);
        }

        public void Unsubscribe(AudioCloneSink sink)
        {
            lock (_sinks)
                _sinks.Remove(sink);
        }
    }
}
