using Framework.Core;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能步骤工厂：按 Config 具体类型分发的唯一 switch（§16.1）。
    /// 步骤类型 = [SerializeReference] 多态 Config 类本身（无枚举、无表）。
    /// 新增步骤 = 新 StepConfig 子类 + 新 Step 类 + 本工厂一行；Ctrl / System / 总线零改动。
    /// </summary>
    public static class MonsterSkillStepFactory
    {
        public static MonsterSkillStep Create(MonsterModel model, MonsterSkillStepConfig config)
        {
            switch (config)
            {
                case null:
                    Logging.Error("[MonsterSkillStepFactory] Create: 步骤 Config 为 null");
                    return null;
                case MonsterAreaHitStepConfig areaHitConfig:
                    return new MonsterAreaHitStep(model, areaHitConfig);
                case MonsterChaseStepConfig chaseConfig:
                    return new MonsterChaseStep(model, chaseConfig);
                case MonsterSlamStepConfig slamConfig:
                    return new MonsterSlamStep(model, slamConfig);
                case MonsterMeleeStepConfig meleeConfig:
                    return new MonsterMeleeStep(model, meleeConfig);
                case MonsterInstantShotStepConfig instantShotConfig:
                    return new MonsterInstantShotStep(model, instantShotConfig);
                case MonsterEffectStepConfig effectConfig:
                    return new MonsterEffectStep(model, effectConfig);
                case MonsterRingWaveStepConfig ringWaveConfig:
                    return new MonsterRingWaveStep(model, ringWaveConfig);
                case MonsterBeamStepConfig beamConfig:
                    return new MonsterBeamStep(model, beamConfig);
                case MonsterBombingStepConfig bombingConfig:
                    return new MonsterBombingStep(model, bombingConfig);
                case MonsterProjectileStepConfig projectileConfig:
                    return new MonsterProjectileStep(model, projectileConfig);
                case MonsterBurrowStepConfig burrowConfig:
                    return new MonsterBurrowStep(model, burrowConfig);
                default:
                    Logging.Warning(
                        $"[MonsterSkillStepFactory] Create: 未知步骤行为，跳过装配 ({config.GetType().Name})"
                    );
                    return null;
            }
        }
    }
}
