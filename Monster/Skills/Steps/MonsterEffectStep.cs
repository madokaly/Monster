using System;

namespace Game.Entities
{
    [Serializable]
    public class MonsterEffectStepConfig : MonsterSkillStepConfig
    {
    }

    /// <summary>
    /// 纯表现占位步骤（§16.4，点式）：无自身字段，进入表现（StepEffects / StepSounds）由基类 Enter 统一播放
    /// （各端本地随机，不与链内其他步骤的结算冲突）。不结算任何伤害——伤害由链内 AreaHit 等步骤表达，
    /// 表现与判定分离（§1.5 事实与表现分流）。
    /// </summary>
    public class MonsterEffectStep : MonsterSkillStep
    {
        public MonsterEffectStep(MonsterModel model, MonsterEffectStepConfig config)
            : base(model, config)
        {
        }

        protected override void OnStepEnter(float elapsed)
        {
            // 无自身逻辑：占位步骤，表现为基类数组
        }
    }
}
