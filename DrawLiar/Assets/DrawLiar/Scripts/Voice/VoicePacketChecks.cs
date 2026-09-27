#if UNITY_EDITOR
using System;
using System.IO;
using Dissonance.Integrations.MirrorIgnorance;
using Mirror;
using UnityEditor;
using UnityEngine;

namespace DrawLiar
{
    public static class VoicePacketChecks
    {
        [MenuItem("Tools/DrawLiar/Validate Voice Packets")]
        public static void Run()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Run packet checks outside Play Mode.");

            foreach (var length in new[] { 0, 1, 255, 1024 })
            {
                var packet = new byte[length + 3];
                packet[0] = (byte)length;
                packet[1] = (byte)(length >> 8);
                for (var i = 0; i < length; i++)
                    packet[i + 2] = (byte)(i % 251);
                packet[packet.Length - 1] = 42;
                var reader = new NetworkReader(new ArraySegment<byte>(packet));
                using (var message = DissonanceNetworkMessageExtensions.Deserialize(reader))
                {
                    Require(message.Data.Count == length, "Voice packet length changed.");
                    Require(reader.Remaining == 1, "Voice reader consumed the following message.");
                    Require(!ReferenceEquals(message.Data.Array, packet), "Voice data must own its buffer.");
                    for (var i = 0; i < length; i++)
                        Require(message.Data.Array[message.Data.Offset + i] == (byte)(i % 251), "Voice payload changed.");
                }
            }

            Reject<InvalidDataException>(new byte[] { 1, 4 });
            Reject<InvalidDataException>(new byte[] { 255, 255 });
            Reject<EndOfStreamException>(new byte[] { 4, 0, 1, 2, 3 });
            Reject<EndOfStreamException>(new byte[] { 1 });
            Debug.Log("Voice packet checks passed: payload boundaries, ownership, trailing data, and malformed lengths.");
        }

        static void Reject<T>(byte[] packet) where T : Exception
        {
            try
            {
                using (DissonanceNetworkMessageExtensions.Deserialize(new NetworkReader(new ArraySegment<byte>(packet)))) { }
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException("Malformed voice packet was accepted: " + typeof(T).Name);
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
#endif
