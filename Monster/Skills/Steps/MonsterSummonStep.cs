using System;
using System.Collections.Generic;
using Framework.Core;
using Game.Components;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterSummonStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("定位基准（召唤物在其位置 + 朝向生成）")]
        public Transform SelfTransform;

        [Tooltip("召唤物 prefab（根节点必须挂 MonsterSummonBehaviour 派生组件）")]
        public GameObject SummonPrefab;

        [Header("Placement")]
        [Tooltip("生成位置偏移（SelfTransform 本地空间）")]
        public Vector3 SpawnOffset = Vector3.zero;

        [Tooltip("伤害窗口时长（= 步骤内容时长，链时序用；必须 > 0——点式语义会让召唤物立即停伤。收尾表现用基类 EndOffset；召唤物自身寿命由行为组件自管）")]
        public float StepDuration = 5f;

        public override float Duration => Mathf.Max(0f, StepDuration);
    }

    /// <summary>
    /// 召唤步骤：各端本地实例化召唤物（本地简易子实体，本地变体——只发不收、
    /// 无自有 Id、不注册 EntityRegistry），行为逻辑全部内聚在 prefab 上的 MonsterSummonBehaviour
    /// 派生组件（自带时间轴 / 表现 / 判定，各端本地自治）。
    /// 步骤职责收敛为「放置 + 生命周期收口」：内容结束（StepDuration）→ StopDamage（停伤不停表现，
    /// 收尾段表现继续）；Exit → 兜底停伤（打断 / 截断收敛，幂等）；
    /// Dispose（实体销毁）→ 销毁全部存活实例（父销毁子必销）。
    /// </summary>
    public class MonsterSummonStep : MonsterSkillStep
    {
        private readonly MonsterSummonStepConfig _summonConfig;

        /// <summary> 存活召唤物（含已停伤待自毁的残留实例，Dispose 时统一回收） </summary>
        private readonly List<MonsterSummonBehaviour> _liveSummons = new();

        public MonsterSummonStep(MonsterModel model, MonsterSummonStepConfig config)
            : base(model, config)
        {
            _summonConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_summonConfig is null) return;

            // 清理已自毁条目（Unity 假 null）；上一次施放的残留实例继续自行收尾
            for (int i = _liveSummons.Count - 1; i >= 0; i--)
            {
                if (_liveSummons[i] == null) _liveSummons.RemoveAt(i);
            }

            var self = _summonConfig.SelfTransform;
            if (self == null || _summonConfig.SummonPrefab == null)
            {
                Logging.Error("[MonsterSummonStep] OnStepEnter: SelfTransform / SummonPrefab 为空，请检查 Inspector 引用。");
                return;
            }

            var summonObj = UnityEngine.Object.Instantiate(
                _summonConfig.SummonPrefab,
                self.position + self.rotation * _summonConfig.SpawnOffset,
                self.rotation
            );

            if (!summonObj.TryGetComponent<MonsterSummonBehaviour>(out var summon))
            {
                Logging.Error(
                    $"[MonsterSummonStep] OnStepEnter: SummonPrefab 缺 MonsterSummonBehaviour 组件 ({_summonConfig.SummonPrefab.name})。"
                );
                UnityEngine.Object.Destroy(summonObj);
                return;
            }

            summon.Initialize(_model.Id);
            _liveSummons.Add(summon);
        }

        protected override void OnStepContentEnded()
        {
            // 停伤不停表现：内容（伤害窗口 StepDuration）结束即停伤，
            // 收尾段（EndOffset）内表现自然播完自毁
            StopAllSummonDamage();
        }

        protected override void OnStepExit()
        {
            // 兜底停伤（打断 / 重叠截断可能发生在内容结束前；StopDamage 幂等）
            StopAllSummonDamage();
        }

        private void StopAllSummonDamage()
        {
            for (int i = 0; i < _liveSummons.Count; i++)
            {
                _liveSummons[i]?.StopDamage();
            }
        }

        protected override void OnStepDispose()
        {
            // 父实体销毁 → 子实体必销毁（生命周期契约）
            for (int i = 0; i < _liveSummons.Count; i++)
            {
                if (_liveSummons[i] != null)
                {
                    UnityEngine.Object.Destroy(_liveSummons[i].gameObject);
                }
            }

            _liveSummons.Clear();
        }
    }
}
