using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能链（Ctrl 上序列化的一条可施放技能；技能数据唯一权威，§16.1 / §16.2）。
    /// 链 = 链级字段（显示名 / 权重 / 冷却 / 射程）+ 顺序步骤列表（纯时间线，无分支）。
    /// </summary>
    [Serializable]
    public class MonsterSkillChain
    {
        [Tooltip("显示名（调试 / 日志用）")]
        public string DisplayName;

        [Tooltip("AI 权重（0 = 不进技能池）")]
        public int Weight = 1;

        [Tooltip("链冷却（秒）")]
        public float CooldownTime;

        [Tooltip("施放距离（AI 距离守卫用）")]
        public float AttackDistance;

        [Header("Enter Presentation（进入技能链时，各随机取一，空则跳过）")]
        [Tooltip("进入动画 Id 数组（随机其一经 AnimId 状态同步全端播放；空 = 不写、保持当前动画）")]
        public int[] EnterAnimIds;

        [Tooltip("进入特效数组（随机其一，各端本地播；空 = 不播）")]
        public MonsterEffectSettings[] EnterEffects;

        [Tooltip("进入音效路径数组（随机其一，各端本地播；空 = 不播；挂点 = SoundAttachPoint）")]
        public string[] EnterSounds;

        [Tooltip("进入音效挂点（EnterSounds 非空时使用；空 = 音效不播）")]
        public Transform SoundAttachPoint;

        [Header("步骤列表（列表顺序即时间线顺序）")]
        [SerializeReference]
        public List<MonsterSkillStepConfig> Steps = new();

        /// <summary>
        /// 链总时长（秒）：派生 max(StartOffset + Duration)，无显式字段（§1.1 单一可信源，避免双源不同步）。
        /// </summary>
        public float CastDuration
        {
            get
            {
                float max = 0f;
                for (int i = 0; i < Steps.Count; i++)
                {
                    var step = Steps[i];
                    if (step == null) continue;
                    max = Mathf.Max(max, step.StartOffset + step.Duration);
                }
                return max;
            }
        }
    }
}
