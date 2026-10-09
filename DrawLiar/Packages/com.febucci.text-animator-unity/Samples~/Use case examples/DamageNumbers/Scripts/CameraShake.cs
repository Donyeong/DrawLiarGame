// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using System.Collections;
using UnityEngine;

namespace Febucci.Examples
{
    public class CameraShake : MonoBehaviour
    {
        [SerializeField] float duration = 0.12f;
        [SerializeField] float strength = 0.08f;

        Vector3 initialLocalPosition;
        Coroutine shakeRoutine;

        void Awake()
        {
            initialLocalPosition = transform.localPosition;
        }

        void OnDisable()
        {
            StopShake();
        }

        public void Play()
        {
            if (shakeRoutine != null)
                StopCoroutine(shakeRoutine);

            transform.localPosition = initialLocalPosition;
            shakeRoutine = StartCoroutine(ShakeRoutine());
        }

        IEnumerator ShakeRoutine()
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;

                float fade = 1f - Mathf.Clamp01(elapsed / duration);
                Vector2 offset = Random.insideUnitCircle * (strength * fade);
                transform.localPosition = initialLocalPosition + new Vector3(offset.x, offset.y, 0f);

                yield return null;
            }

            StopShake();
        }

        void StopShake()
        {
            if (shakeRoutine != null)
            {
                StopCoroutine(shakeRoutine);
                shakeRoutine = null;
            }

            transform.localPosition = initialLocalPosition;
        }
    }
}
