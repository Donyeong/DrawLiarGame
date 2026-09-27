#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Threading;
using Dissonance.Audio.Playback;
using UnityEngine;

namespace DrawLiar
{
    public sealed class DrawVoicePlaybackProbe : MonoBehaviour, IAudioOutputSubscriber
    {
        private double energy;
        private long samples;
        private int callbacks;
        private float lastPeak;

        public double Energy => Volatile.Read(ref energy);
        public long Samples => Interlocked.Read(ref samples);
        public int Callbacks => Volatile.Read(ref callbacks);
        public float LastPeak => Volatile.Read(ref lastPeak);

        // Dissonance calls this after decoding and per-peer gain, before the Unity listener.
        // Audio thread: no allocation, locks, logging, or Unity API calls.
        public void OnAudioPlayback(ArraySegment<float> data, bool complete)
        {
            double blockEnergy = 0;
            float peak = 0;
            for (int i = data.Offset; i < data.Offset + data.Count; i++)
            {
                float sample = data.Array[i];
                blockEnergy += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            Volatile.Write(ref energy, energy + blockEnergy);
            Interlocked.Add(ref samples, data.Count);
            Volatile.Write(ref lastPeak, peak);
            Interlocked.Increment(ref callbacks);
        }
    }
}
#endif
