// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using System.Collections;
using Febucci.TextAnimatorForUnity;
using TMPro;
using UnityEngine;

namespace Febucci.Examples
{
    public class DamageNumber : MonoBehaviour
    {
        [SerializeField] TextAnimatorComponentBase textAnimator;
        [SerializeField] float floatSpeed = 1.5f;

        Transform cachedTransform;
        TMP_Text tmpText;

        private void Awake()
        {
            cachedTransform = transform;
            tmpText = GetComponent<TMP_Text>();

            textAnimator.SetText(string.Empty); // clears for next activation
            SetAlpha(0f);
        }

        public IEnumerator Show(float lifetime, string text, Vector3 pos, Vector3 direction)
        {
            // --- Initialization ---
            SetAlpha(1f);
            cachedTransform.position = pos;
            
            textAnimator.SetText(text);

            // --- Moves up and fades ---
            float elapsed = 0f;
            while (elapsed <= lifetime)
            {
                elapsed += Time.deltaTime;
                cachedTransform.position += direction * (floatSpeed * Time.deltaTime);
                SetAlpha(1f - (elapsed / lifetime));
                yield return null;
            }
            
            // Finishes
            textAnimator.SetText(string.Empty); // clears for next activation
            SetAlpha(0f);
            gameObject.SetActive(false);
        }

        void SetAlpha(float alpha)
        {
            if (!tmpText)
                return;

            tmpText.alpha = alpha;
        }
    }
}
