using System;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能原子步骤配置基类（链内序列化的行为单元；[SerializeReference] 多态）。
    /// 步骤自带该行为的数值 / 时序 / 表现引用（不经配置表，§16.2）。
    /// 新增步骤 = 新 StepConfig 子类 + 新 Step 类 + 工厂一行。
    /// </summary>
    [Serializable]
    public abstract class MonsterSkillStepConfig
    {
        [Tooltip("链内起始偏移（秒，施放起点起算）")]
        public float StartOffset;

        [Header("Enter Presentation（进入步骤时，各随机取一，空则跳过）")]
        [Tooltip("步骤动画 Id 数组（随机其一，权威端经 AnimId 状态同步全端播放，替换当前动画；空 = 不写、保持当前动画）")]
        public int[] StepAnimIds;

        [Tooltip("步骤特效数组（随机其一，各端本地播，与链级特效叠加；空 = 不播）")]
        public MonsterEffectSettings[] StepEffects;

        [Tooltip("步骤音效路径数组（随机其一，各端本地播，与链级音效叠加；空 = 不播；挂点 = SoundAttachPoint）")]
        public string[] StepSounds;

        [Tooltip("步骤音效挂点（StepSounds 非空时使用；空 = 音效不播）")]
        public Transform SoundAttachPoint;

        /// <summary>
        /// 步骤时长（秒）。窗口式步骤（&gt;0）在 [StartOffset, StartOffset+Duration] 内逐 tick 结算；
        /// 点式步骤（0）在 StartOffset 时刻一次性结算（Enter 即 Exit）。
        /// 链总时长 = max(StartOffset + Duration)。
        /// </summary>
        public virtual float Duration => 0f;
    }
}
