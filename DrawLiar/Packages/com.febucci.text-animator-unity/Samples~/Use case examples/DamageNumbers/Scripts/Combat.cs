// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using Febucci.Examples;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
#endif

public class Combat : MonoBehaviour
{
    private static readonly int HashHit = Animator.StringToHash("Hit");
    [SerializeField] GameObject enemy;
    [SerializeField] Animator enemyAnimator;
    [SerializeField] Animator impactAnimator;
    [SerializeField] CameraShake cameraShake;
    [SerializeField] private string prefixWhenCrit = "<cshake instant f=10><rainb>";
    [SerializeField] private string prefixWhenNormalDamage;
    
    [SerializeField] int[] damageValues = { 10, 25, 50, 120, 300, 500, 999 };
    [SerializeField] int critThreshold = 200;

    void Awake()
    {
        if (!cameraShake && Camera.main)
            cameraShake = Camera.main.GetComponent<CameraShake>();
#if ENABLE_INPUT_SYSTEM
        UnityEngine.InputSystem.InputSystem.onAnyButtonPress.Call(OnAnyInputPressed);
#endif
    }

    public void Hit()
    {
        int damage = damageValues[Random.Range(0, damageValues.Length)];
        string text = damage >= critThreshold
            ? $"{prefixWhenCrit}CRIT</> {damage}"
            : $"{prefixWhenNormalDamage}{damage.ToString()}";

        DamageNumberController.instance.Spawn(enemy.transform.position, text);
        enemyAnimator.SetTrigger(HashHit);
        if (!impactAnimator.gameObject.activeSelf)
            impactAnimator.gameObject.SetActive(true);
        impactAnimator.SetTrigger(HashHit);
        cameraShake?.Play();
    }
    
    
#if ENABLE_INPUT_SYSTEM
    void OnAnyInputPressed(UnityEngine.InputSystem.InputControl control)
    {
        if(Mouse.current != null && control.device == Mouse.current)
            return; // skips mouse
        
        pressed = true;
    }    
#endif

    bool pressed = false;
    private void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        pressed |= Input.GetKeyDown(KeyCode.Space);
#endif
        
        if(pressed)
        {
            Hit();
        }

        pressed = false;
    }
}
