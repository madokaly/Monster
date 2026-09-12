using System;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能原子步骤配置基类（链内序列化的行为单元；[SerializeReference] 多态）。
    /// 步骤自带该行为的数值 / 时序 / 表现引用（不经配置表）。
    /// 新增步骤 = 新 StepConfig 子类 + 新 Step 类 + 工厂一行。
    /// </summary>
    [Serializable]
    public abstract class MonsterSkillStepConfig
    {
        [Tooltip("链内起始偏移（秒，施放起点起算）")]
        public float StartOffset;

        [Tooltip("收尾延迟（秒；步骤内容（Duration）结束后步骤继续保持活跃的时长——留收尾表现 / 特效余韵，到期才 Exit。0 = 内容结束即退出）")]
        public float EndOffset;

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
        /// 步骤内容时长（秒），由各步骤自身时序字段派生（单一可信源）。
        /// 窗口式步骤（&gt;0）的内容逻辑（伤害窗口 / 相位 / 追结）在 [StartOffset, StartOffset+Duration] 内完成；
        /// 点式步骤（0）在 StartOffset 时刻一次性结算。不含收尾延迟。
        /// </summary>
        public virtual float Duration => 0f;

        /// <summary>
        /// 步骤总窗口时长（秒）= 内容时长 + 收尾延迟。
        /// 步骤在 [StartOffset, StartOffset+TotalDuration] 内保持活跃：内容结束后进入收尾段
        /// （表现继续、Exit 清理延迟到窗口结束；伤害类逻辑随内容结束停止）。
        /// 链总时长 = max(StartOffset + TotalDuration)。
        /// </summary>
        public float TotalDuration => Duration + Mathf.Max(0f, EndOffset);
    }
}
