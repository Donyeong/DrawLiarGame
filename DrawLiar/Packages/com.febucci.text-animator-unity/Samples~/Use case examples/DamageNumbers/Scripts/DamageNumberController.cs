// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Febucci.Examples
{
    public class DamageNumberController : MonoBehaviour
    {
        public static DamageNumberController instance;
        
        [SerializeField] GameObject prefab;
        [SerializeField] int initialPoolSize = 5;
        [SerializeField] float defaultLifetime = 1.5f;
        [SerializeField, Range(0.0f, 1.0f)] float horizontalSpread = 1f;
        [SerializeField] Vector2 damageNumberOffset = new Vector2(0f, 0.75f);

        readonly Queue<DamageNumber> pool = new Queue<DamageNumber>();

        void Awake()
        {
            instance = this;
            
            for (int i = 0; i < initialPoolSize; i++)
                pool.Enqueue(CreateInstance());
        }

        public void Spawn(Vector3 worldPosition, string text) =>
            StartCoroutine(SpawnRoutine(worldPosition, text));

        IEnumerator SpawnRoutine(Vector3 worldPos, string text)
        {
            Vector3 spawnPosition = worldPos + new Vector3(damageNumberOffset.x, damageNumberOffset.y, 0f);

            DamageNumber number = GetAndRemoveFromPool();
            number.gameObject.SetActive(true);
            
            yield return number.Show(
                defaultLifetime,
                text,
                spawnPosition,
                new Vector3(Random.Range(-horizontalSpread, horizontalSpread), 1f, 0f).normalized);
            
            pool.Enqueue(number); // returns back to pool
        }
        
        DamageNumber CreateInstance()
        {
            GameObject go = Instantiate(prefab, transform);
            go.SetActive(false);
            return go.GetComponent<DamageNumber>();
        }

        DamageNumber GetAndRemoveFromPool()
        {
            return pool.Count > 0 ? pool.Dequeue() : CreateInstance();
        }
    }
}
