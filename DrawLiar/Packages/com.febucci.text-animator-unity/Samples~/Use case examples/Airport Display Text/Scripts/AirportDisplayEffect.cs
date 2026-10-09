// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using System.Collections;
using System.Collections.Generic;
using System.Text;
using Febucci.TextAnimatorForUnity;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
#endif

namespace Febucci.Examples
{
    public class AirportDisplayEffect : MonoBehaviour
    {
        [SerializeField] TextAnimatorComponentBase textAnimator;

        [Header("Airport Display Settings")]
        [Tooltip("Time in seconds between each random character cycle")]
        [SerializeField] float cycleInterval = 0.03f;
        [Tooltip("Time in seconds before the next character settles to its final value")]
        [SerializeField] float settleInterval = 0.05f;
        [Tooltip("Pool of characters used for the random cycling effect")]
        [SerializeField] string charPool = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        [Tooltip("List of texts to cycle through when input is pressed")]
        [TextArea]
        [SerializeField] List<string> textToShow;

        int currentTextIndex = 0;
        Coroutine activeCoroutine;
        readonly StringBuilder sb = new();
        bool pressed;

        void Start()
        {
            SetNextText();
#if ENABLE_INPUT_SYSTEM
            InputSystem.onAnyButtonPress.Call(OnAnyInputPressed);
#endif
        }

#if ENABLE_INPUT_SYSTEM
        void OnAnyInputPressed(InputControl control)
        {
            if (Mouse.current != null && control.device == Mouse.current)
                return; // skips mouse

            pressed = true;
        }
#endif

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            pressed |= Input.GetKeyDown(KeyCode.Space);
#endif

            if (pressed)
            {
                SetNextText();
            }

            pressed = false;
        }

        public void SetNextText()
        {
            if (textToShow.Count <= 0)
                return;

            if (activeCoroutine != null)
                StopCoroutine(activeCoroutine);
            
            // starts coroutine with new text
            var text = textToShow[currentTextIndex];
            activeCoroutine = StartCoroutine(FlipCoroutine(text));

            currentTextIndex = (currentTextIndex + 1) % textToShow.Count;
        }

        IEnumerator FlipCoroutine(string targetText)
        {
            int visibleCount = CountVisibleChars(targetText);
            int settledCount = 0;
            float settleTimer = 0f;

            while (settledCount < visibleCount)
            {
                string displayText = BuildDisplayText(targetText, settledCount);
                textAnimator.SetText(displayText);

                yield return new WaitForSeconds(cycleInterval);

                settleTimer += cycleInterval;
                if (settleTimer >= settleInterval)
                {
                    settleTimer = 0f;
                    settledCount = Mathf.Min(settledCount + 1, visibleCount);
                }
            }

            // Final text with all characters settled
            textAnimator.SetText(targetText);
            activeCoroutine = null;
        }

        /// <summary>
        /// Builds the display string for the current frame of the flip effect.
        /// The first <paramref name="settledCount"/> visible characters are kept as-is,
        /// while all remaining visible characters are replaced with a random character
        /// from <see cref="charPool"/>. Tags and whitespace are passed through unchanged.
        /// </summary>
        string BuildDisplayText(string text, int settledCount)
        {
            sb.Clear();
            int visibleIndex = 0;
            bool inTag = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '<' && !inTag)
                {
                    inTag = true;
                    sb.Append(c);
                    continue;
                }

                if (c == '>' && inTag)
                {
                    inTag = false;
                    sb.Append(c);
                    continue;
                }

                if (inTag)
                {
                    sb.Append(c);
                    continue;
                }

                // Visible character
                if (visibleIndex < settledCount || char.IsWhiteSpace(c))
                    sb.Append(c);
                else
                    sb.Append(charPool[Random.Range(0, charPool.Length)]);

                if (!char.IsWhiteSpace(c))
                    visibleIndex++;
            }

            return sb.ToString();
        }


        /// <summary>
        /// Returns the number of characters of the text, skipping TextAnimator's tags (e.g. <shake></shake>, etc...).
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        static int CountVisibleChars(string text)
        {
            int count = 0;
            bool inTag = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<' && !inTag) { inTag = true; continue; }
                if (c == '>' && inTag) { inTag = false; continue; }
                if (!inTag && !char.IsWhiteSpace(c)) count++;
            }
            return count;
        }
    }
}
