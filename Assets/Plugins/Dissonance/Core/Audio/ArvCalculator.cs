using System;

namespace Dissonance.Audio
{
    internal struct ArvCalculator
    {
        public float ARV { get; private set; }

        public void Reset()
        {
            ARV = 0;
        }

        public void Update(ReadOnlySpan<float> samples)
        {
            float sum = 0;
            for (var i = 0; i < samples.Length; i++)
                sum += Math.Abs(samples[i]);

            ARV = sum / Math.Max(1, samples.Length);
        }
    }
}
