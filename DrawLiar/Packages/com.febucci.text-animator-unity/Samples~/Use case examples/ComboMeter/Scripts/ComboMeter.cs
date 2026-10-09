// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using Febucci.TextAnimatorForUnity;
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Febucci.Examples
{
    /// <summary>
    /// Keeps the combo text in sync with the current hit count, adding livelier effects as the streak grows.
    /// </summary>
    public class ComboMeter : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Button that triggers the combo counter")]
        [SerializeField] Button button;
        [Tooltip("Typewriter component used to display the combo text")]
        [SerializeField] TypewriterComponent typewriter;

        [Header("Combo Settings")]
        [Tooltip("Text displayed at each combo milestone")]
        [SerializeField] string[] comboStrings = {
            "Hit", "Wow", "Awesome", "Combo Breaker"
        };

        [Header("Effects")]
        [Tooltip("Pop effects randomly applied to the combo text")]
        [SerializeField] string[] popEffects = { "<wavepop>", "<sizepop>", "<slidepop>", "<wigglepop>", "<shakepop>" };

        // State
        int counter = 0;
        int currentComboString = 0;

        // Effect tags
        const string SHAKE_EFFECT = "<shake>";
        const string FLASH_EFFECT = "<comboflash>";
        const string WAVE_EFFECT = "<wave>";

        public void IncreaseCounter()
        {
            // Count this hit before updating the displayed combo.
            counter += 1;

            var effects = "";
            // Pick one or more pop effects to make each hit feel varied.
            var popStackCount = UnityEngine.Random.Range(1, popEffects.Length);

            if (popStackCount <= 1 )
            {
                effects += popEffects[UnityEngine.Random.Range(0, popEffects.Length)];
            }
            else
            {
                for (int i = 0; i < UnityEngine.Random.Range(1, popEffects.Length); i++)
                    effects += popEffects[UnityEngine.Random.Range(0, popEffects.Length)];
            }

            // Add stronger effects as the combo grows.
            if (counter >= 5)
                effects += SHAKE_EFFECT;
            if (counter >= 10)
                effects += FLASH_EFFECT;
            if (counter >= 15)
                effects += WAVE_EFFECT;

            // Move to the next label every five hits.
            if (counter % 5 == 0)
                currentComboString += 1;

            // Show the counter, milestone label, and accumulated effect tags.
            typewriter.ShowText($"{effects}{counter} {comboStrings[Mathf.Clamp(currentComboString, 0, comboStrings.Length - 1)]}");
        }

        public void ResetCombo()
        {
            // Clear combo state and play the disappearance animation.
            counter = 0;
            currentComboString = 0;
            typewriter.StartDisappearingText();
        }

    }
}
