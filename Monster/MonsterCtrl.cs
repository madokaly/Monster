using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Network;
using Fusion;
using Game.Components;
using Game.DTOs;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// Monster 实体控制器：模块装配与派发、跨实体消息路由入口。
    /// 不实现具体业务（AI / 技能 / 移动一律进 Module），不订阅 Model / 组件事件。
    /// </summary>
    public class MonsterCtrl : NetworkBehaviour, IStateAuthorityChanged
    {
        [Title("Model")]
        [SerializeField]
        private MonsterModel _model;

        [TitleGroup("Modules")]
        [SerializeField]
        [BoxGroup("Modules/AI Module", centerLabel: true), HideLabel]
        private MonsterAIModuleConfig _aiModuleConfig;

        [SerializeField]
        [BoxGroup("Modules/Perception Module", centerLabel: true), HideLabel]
        private MonsterPerceptionModuleConfig _perceptionModuleConfig;

        [SerializeField]
        [BoxGroup("Modules/Move Module", centerLabel: true), HideLabel]
        private MonsterMoveModuleConfig _moveModuleConfig;

        [SerializeField]
        [BoxGroup("Modules/Render Module", centerLabel: true), HideLabel]
        private MonsterRenderModuleConfig _renderModuleConfig;

        [SerializeField]
        [BoxGroup("Modules/BattleView Module", centerLabel: true), HideLabel]
        private MonsterBattleViewModuleConfig _battleViewModuleConfig;

        [SerializeField]
        [BoxGroup("Modules/Zone Authority Module", centerLabel: true), HideLabel]
        private ZoneAuthorityModuleConfig _zoneAuthorityModuleConfig;

        [SerializeField]
        [BoxGroup("Modules/Skill Modules", centerLabel: true), HideLabel]
        [Tooltip("技能链列表（每条链 = 权重/冷却/射程 + 顺序原子步骤；技能数据唯一权威，§16.2）")]
        private List<MonsterSkillChain> _skillChains;

        private MonsterAIModule _aiModule;
        private MonsterPerceptionModule _perceptionModule;
        private MonsterMoveModule _moveModule;
        private MonsterRenderModule _renderModule;
        private MonsterBattleViewModule _battleViewModule;
        private ZoneAuthorityModule _zoneAuthorityModule;
        private readonly List<MonsterSkillChainRunner> _chainRunners = new();

        /// <summary> 销毁请求已发出标记（幂等：Spawner 轮询 / 多来源重复发送只转发一次） </summary>
        private bool _despawnRequested;

        /// <summary> 实体 Id </summary>
        public new EntityId Id => Object != null ? Object.Id : default;

        /// <summary> 怪物配置 Id </summary>
        public int CfgId => _model != null ? _model.Template.CfgId : 0;

        /// <summary> 出生点 SpawnId </summary>
        public int SpawnId => _model != null ? _model.Template.SpawnId : 0;

        #region Lifecycle

        public void PreInit(MonsterTemplate template)
        {
            if (_model == null) _model = GetComponent<MonsterModel>();
            if (_model == null)
            {
                Logging.Error("[MonsterCtrl] PreInit: 缺少 MonsterModel 组件");
                _model = null; // 方便后续用 `?` / `is null` 判空，不走 Unity 非托管层，节省开销
                return;
            }

            _model.PreInit(template);
        }

        public override void Spawned()
        {
            // 1. 获取 Model
            if (_model == null) _model = GetComponent<MonsterModel>();
            if (_model == null)
            {
                Logging.Error("[MonsterCtrl] Spawned: 缺少 MonsterModel 组件");
                _model = null; // 方便后续用 `?` / `is null` 判空，不走 Unity 非托管层，节省开销
                return;
            }

            // 2. 创建 Modules（通用模块恒定；技能链经校验后装配链运行器）
            _aiModule = new MonsterAIModule(_model, _aiModuleConfig);
            _perceptionModule = new MonsterPerceptionModule(_model, _perceptionModuleConfig);
            _moveModule = new MonsterMoveModule(_model, _moveModuleConfig);
            _renderModule = new MonsterRenderModule(_model, _renderModuleConfig);
            _battleViewModule = new MonsterBattleViewModule(_model, _battleViewModuleConfig);
            _zoneAuthorityModule = new ZoneAuthorityModule(_model, _zoneAuthorityModuleConfig);

            AssembleSkillChains();

            // 3. 注册到实体中心
            EntityRegistry.Register(Id, _model.Template);

            // 4. 末尾发送生成完成消息
            Msger.Send(MsgID.MonsterSpawned, Id, SpawnId);

            // 5. 末尾 StateAuthorityChanged 刷新权威状态
            StateAuthorityChanged();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            // 1. 释放所有 Modules
            _aiModule?.Dispose();
            _perceptionModule?.Dispose();
            _moveModule?.Dispose();
            _renderModule?.Dispose();
            _battleViewModule?.Dispose();
            _zoneAuthorityModule?.Dispose();

            for (int i = 0; i < _chainRunners.Count; i++)
            {
                _chainRunners[i]?.Dispose();
            }
            _chainRunners.Clear();

            // 2. 从实体中心反注册
            EntityRegistry.Unregister(Id);

            // 3. 末尾发销毁完成消息
            Msger.Send(MsgID.MonsterDespawned, Id);
        }

        public void StateAuthorityChanged()
        {
            bool hasStateAuthority = HasStateAuthority;
            // 刷新所有 Modules 的权威状态
            _aiModule?.StateAuthorityChanged(hasStateAuthority);
            _perceptionModule?.StateAuthorityChanged(hasStateAuthority);
            _moveModule?.StateAuthorityChanged(hasStateAuthority);
            _renderModule?.StateAuthorityChanged(hasStateAuthority);
            _battleViewModule?.StateAuthorityChanged(hasStateAuthority);
            _zoneAuthorityModule?.StateAuthorityChanged(hasStateAuthority);

            for (int i = 0; i < _chainRunners.Count; i++)
            {
                _chainRunners[i]?.StateAuthorityChanged(hasStateAuthority);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            MonsterPerceptionModule.DrawGizmos(_model, _perceptionModuleConfig, transform);
        }
#endif

        private void FixedUpdate()
        {
            // 协调所有 Modules 的 FixedUpdate
            float fixedDeltaTime = Time.fixedDeltaTime;

            _aiModule?.FixedUpdate(fixedDeltaTime);
            _perceptionModule?.FixedUpdate(fixedDeltaTime);
            _moveModule?.FixedUpdate(fixedDeltaTime);
            _renderModule?.FixedUpdate(fixedDeltaTime);
            _battleViewModule?.FixedUpdate(fixedDeltaTime);
            _zoneAuthorityModule?.FixedUpdate(fixedDeltaTime);

            for (int i = 0; i < _chainRunners.Count; i++)
            {
                _chainRunners[i]?.FixedUpdate(fixedDeltaTime);
            }
        }

        public override void FixedUpdateNetwork()
        {
            // 协调所有 Modules 的 FUN
            float deltaTime = Runner.DeltaTime;

            _aiModule?.FixedUpdateNetwork(deltaTime);
            _perceptionModule?.FixedUpdateNetwork(deltaTime);
            _moveModule?.FixedUpdateNetwork(deltaTime);
            _renderModule?.FixedUpdateNetwork(deltaTime);
            _battleViewModule?.FixedUpdateNetwork(deltaTime);
            _zoneAuthorityModule?.FixedUpdateNetwork(deltaTime);

            for (int i = 0; i < _chainRunners.Count; i++)
            {
                _chainRunners[i]?.FixedUpdateNetwork(deltaTime);
            }
        }

        private void Update()
        {
            // 协调所有 Modules 的 Update
            float deltaTime = Time.deltaTime;

            _aiModule?.Update(deltaTime);
            _perceptionModule?.Update(deltaTime);
            _moveModule?.Update(deltaTime);
            _renderModule?.Update(deltaTime);
            _battleViewModule?.Update(deltaTime);
            _zoneAuthorityModule?.Update(deltaTime);

            for (int i = 0; i < _chainRunners.Count; i++)
            {
                _chainRunners[i]?.Update(deltaTime);
            }
        }

        private void LateUpdate()
        {
            // 协调所有 Modules 的 LateUpdate
            float deltaTime = Time.deltaTime;

            _aiModule?.LateUpdate(deltaTime);
            _perceptionModule?.LateUpdate(deltaTime);
            _moveModule?.LateUpdate(deltaTime);
            _renderModule?.LateUpdate(deltaTime);
            _battleViewModule?.LateUpdate(deltaTime);
            _zoneAuthorityModule?.LateUpdate(deltaTime);

            for (int i = 0; i < _chainRunners.Count; i++)
            {
                _chainRunners[i]?.LateUpdate(deltaTime);
            }
        }

        #endregion

        #region Query API

        /// <summary>
        /// 查询：位置
        /// </summary>
        public Vector3 QueryPosition()
        {
            return _moveModule?.QueryPosition() ?? default;
        }

        /// <summary>
        /// 查询：旋转
        /// </summary>
        public Quaternion QueryRotation()
        {
            return _moveModule?.QueryRotation() ?? default;
        }

        /// <summary>
        /// 查询：是否死亡
        /// </summary>
        public bool QueryIsDead()
        {
            if (_model is null) return true;
            return _model.IsDead();
        }

        #endregion

        #region Command API

        /// <summary>
        /// 请求：传送
        /// </summary>
        public void RequestTeleport(Vector3 position, Quaternion rotation)
        {
            if (!HasStateAuthority)
            {
                RPC_RequestTeleport(position, rotation);
                return;
            }

            if (_model is null) return;

            _model.PushTeleportPosition(position);
            _model.PushTeleportRotation(rotation);
        }

        /// <summary>
        /// 请求：受到伤害（调用点需要跨端必达权威：命中检测各端本地进行）
        /// </summary>
        public void RequestApplyDamage(DamageData damageData)
        {
            if (!HasStateAuthority)
            {
                RPC_RequestApplyDamage(damageData);
                return;
            }

            if (_model is null) return;

            _model.TakeDamage(damageData.Damage, damageData.AttackerId);
        }

        /// <summary>
        /// 请求：受击表现
        /// </summary>
        public void RequestApplyHit(HitData damageData)
        {
            if (!HasStateAuthority)
            {
                RPC_RequestApplyHit(damageData);
                return;
            }

            if (_model is null) return;

            _model.PushHitReceived(damageData);
        }

        /// <summary>
        /// 请求：销毁（统一销毁模式 §8——调用点可能在任何端；非权威端 RPC 转发，权威端执行 Despawn）。
        /// SA=None 特权分支（§16.4 第三层）：RPC 无目标，MC 直接 Despawn（prefab 需勾选 Allow State Authority Override）。
        /// 幂等守卫：销毁轮询 / 多来源重复发送只转发一次。
        /// </summary>
        public void RequestDespawn()
        {
            if (_despawnRequested) return;

            if (!HasStateAuthority)
            {
                // SA=None 时 RPC 无目标：仅 MC 可行使无 SA Despawn 特权（§16.4 第三层）
                if (Object.StateAuthority.IsNone)
                {
                    if (!NetworkMgr.IsMasterClient) return;
                    _despawnRequested = true;
                    if (Object == null || !Object.IsValid) return;
                    NetworkMgr.Despawn(Object);
                    return;
                }

                _despawnRequested = true;
                RPC_RequestDespawn();
                return;
            }

            if (Object == null || !Object.IsValid) return;
            _despawnRequested = true;
            NetworkMgr.Despawn(Object);
        }

        #endregion

        #region Message API

        #endregion

        #region RPCs

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_RequestTeleport(Vector3Compressed position, QuaternionCompressed rotation)
        {
            RequestTeleport(position, rotation);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_RequestApplyDamage(DamageData damageData)
        {
            RequestApplyDamage(damageData);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_RequestApplyHit(HitData damageData)
        {
            RequestApplyHit(damageData);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_RequestDespawn()
        {
            RequestDespawn();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 技能链装配（Spawned 五步中的第 2 步，§16.5）：
        /// 逐链校验（步骤非空 / 时序合法 / 动画与数值合法），校验通过经工厂装配链运行器，
        /// 装配结果（链列表 + 可用链索引）注入 Model（AI 守卫用）。
        /// </summary>
        private void AssembleSkillChains()
        {
            var availableChainIndices = new HashSet<int>();

            if (_skillChains == null)
            {
                _model.SetSkillChains(null, availableChainIndices);
                return;
            }

            for (int i = 0; i < _skillChains.Count; i++)
            {
                var chain = _skillChains[i];
                if (chain == null)
                {
                    Logging.Error(
                        $"[MonsterCtrl] AssembleSkillChains: 链配置为 null (chainIndex: {i}, monster: {_model.Template.CfgId})"
                    );
                    continue;
                }

                if (!ValidateChain(i, chain)) continue;

                _chainRunners.Add(new MonsterSkillChainRunner(_model, i, chain));
                availableChainIndices.Add(i);
            }

            // AI 守卫：只有装配成功（运行器已创建）的链才可选
            _model.SetSkillChains(_skillChains, availableChainIndices);
        }

        /// <summary>
        /// 链结构校验（§16.5）：步骤列表非空 / 步骤非 null / 偏移非负且非降序（列表顺序即时间线顺序）/
        /// 动画与时长合法 / 权重 / 冷却 / 射程合法。异常报错并拒绝装配该链。
        /// </summary>
        private bool ValidateChain(int chainIndex, MonsterSkillChain chain)
        {
            bool valid = true;
            string chainLabel = $"(chainIndex: {chainIndex}, monster: {_model.Template.CfgId})";

            if (chain.Weight < 0)
            {
                Logging.Error($"[MonsterCtrl] ValidateChain: 权重非法 {chain.Weight} {chainLabel}");
                valid = false;
            }

            if (chain.CooldownTime < 0f)
            {
                Logging.Error($"[MonsterCtrl] ValidateChain: 冷却非法 {chain.CooldownTime} {chainLabel}");
                valid = false;
            }

            if (chain.AttackDistance < 0f)
            {
                Logging.Error($"[MonsterCtrl] ValidateChain: 射程非法 {chain.AttackDistance} {chainLabel}");
                valid = false;
            }

            if (chain.EnterAnimIds != null)
            {
                for (int j = 0; j < chain.EnterAnimIds.Length; j++)
                {
                    if (chain.EnterAnimIds[j] >= 0) continue;

                    Logging.Error($"[MonsterCtrl] ValidateChain: 链级进入动画非法 ({chain.EnterAnimIds[j]}) {chainLabel}");
                    valid = false;
                }
            }

            if (chain.Steps is not { Count: > 0 })
            {
                Logging.Error($"[MonsterCtrl] ValidateChain: 步骤列表为空 {chainLabel}");
                return false;
            }

            float lastStartOffset = -1f;
            MonsterSkillStepConfig previousStep = null;
            for (int j = 0; j < chain.Steps.Count; j++)
            {
                var step = chain.Steps[j];
                if (step == null)
                {
                    Logging.Error($"[MonsterCtrl] ValidateChain: 步骤为 null (stepIndex: {j}) {chainLabel}");
                    valid = false;
                    continue;
                }

                if (step.StartOffset < 0f)
                {
                    Logging.Error($"[MonsterCtrl] ValidateChain: 步骤起始偏移为负 ({step.StartOffset}) {chainLabel}");
                    valid = false;
                }

                if (step.StartOffset < lastStartOffset)
                {
                    Logging.Error(
                        $"[MonsterCtrl] ValidateChain: 步骤时序乱序 (stepIndex: {j}, startOffset: {step.StartOffset} < {lastStartOffset}) {chainLabel}"
                    );
                    valid = false;
                }

                if (step.Duration < 0f)
                {
                    Logging.Error($"[MonsterCtrl] ValidateChain: 步骤时长为负 (stepIndex: {j}) {chainLabel}");
                    valid = false;
                }

                if (step.EndOffset < 0f)
                {
                    Logging.Error($"[MonsterCtrl] ValidateChain: 步骤收尾延迟为负 (stepIndex: {j}, endOffset: {step.EndOffset}) {chainLabel}");
                    valid = false;
                }

                // 近战步骤窗口字段校验（时长由相位派生，字段级非法直接拒绝装配）
                if (step is MonsterMeleeStepConfig meleeStep
                    && (meleeStep.HitDelay < 0f || meleeStep.Window <= 0f || meleeStep.RecoveryDuration < 0f))
                {
                    Logging.Error(
                        $"[MonsterCtrl] ValidateChain: 近战步骤窗口非法 (stepIndex: {j}, hitDelay: {meleeStep.HitDelay}, window: {meleeStep.Window}, recovery: {meleeStep.RecoveryDuration}) {chainLabel}"
                    );
                    valid = false;
                }

                // 召唤步骤装配校验（引用缺失运行时报错；窗口必须为正——点式语义会让召唤物立即停伤）
                if (step is MonsterSummonStepConfig summonStep
                    && (summonStep.SelfTransform == null || summonStep.SummonPrefab == null || summonStep.Duration <= 0f))
                {
                    Logging.Error(
                        $"[MonsterCtrl] ValidateChain: 召唤步骤装配非法 (stepIndex: {j}, selfTransform 为空: {summonStep.SelfTransform == null}, summonPrefab 为空: {summonStep.SummonPrefab == null}, duration: {summonStep.Duration}) {chainLabel}"
                    );
                    valid = false;
                }

                if (step.StepAnimIds != null)
                {
                    for (int k = 0; k < step.StepAnimIds.Length; k++)
                    {
                        if (step.StepAnimIds[k] >= 0) continue;

                        Logging.Error(
                            $"[MonsterCtrl] ValidateChain: 步骤动画非法 (stepIndex: {j}, animId: {step.StepAnimIds[k]}) {chainLabel}"
                        );
                        valid = false;
                    }
                }

                // 窗口重叠告警（按总窗口 = 内容 + 收尾延迟）：后续步骤在前序步骤窗口内进入
                // → 运行器重叠守卫会提前截断前序步骤（含收尾段被打断）
                if (previousStep != null
                    && previousStep.TotalDuration > 0f
                    && step.StartOffset < previousStep.StartOffset + previousStep.TotalDuration)
                {
                    Logging.Warning(
                        $"[MonsterCtrl] ValidateChain: 步骤窗口重叠 (stepIndex: {j}, startOffset: {step.StartOffset} 落在前序步骤 [{previousStep.StartOffset}, {previousStep.StartOffset + previousStep.TotalDuration}) 内，前序步骤（含收尾段）将被提前截断) {chainLabel}"
                    );
                }

                lastStartOffset = step.StartOffset;
                previousStep = step;
            }

            return valid;
        }

        #endregion

#if UNITY_EDITOR || DEVELOPMENT_BUILD

        #region Debug (Test Only)

        /// <summary> 测试用：分次攻击次数（模拟玩家连续命中，让受击反应 + 死亡动画完整播放） </summary>
        private const int DEBUG_KILL_HIT_COUNT = 8;
        /// <summary> 测试用：分次攻击间隔（秒）。受击反应节流 0.1s，稍大些让动画可读 </summary>
        private const float DEBUG_KILL_HIT_INTERVAL = 0.15f;

        /// <summary>
        /// 测试用：模拟"被本地玩家连击致死"的标准死亡流程（Odin [Button]，Inspector 上直接显示）。
        /// <para>走完整链路：本地玩家作为攻击者 → 分 8 次伤害让受击反应逐次播放 → HP 归零触发死亡动画 →
        /// <c>DiedAnimTimer</c> 到期播放爆炸 + 发送 <c>MonsterDied</c> → <c>MonsterSpawnerSystem</c> 销毁 + 碎块 + 5s 补怪。</para>
        /// <para>非权威端经 <c>RPC_RequestApplyDamage</c> 转发（§1.7）。仅 Editor / Dev build 编译。</para>
        /// </summary>
        [Button("Kill (Test)", ButtonSizes.Medium)]
        public void DebugKill()
        {
            if (_model == null)
            {
                Logging.Warning("[MonsterCtrl.DebugKill] 怪物 Model 未初始化（PreInit 未执行或编辑器非 Play 模式）");
                return;
            }

            if (_model.IsDead())
            {
                Logging.Info($"[MonsterCtrl.DebugKill] 怪物已死亡 (id: {Id})，跳过");
                return;
            }

            DebugKillSequenceAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 分次施加伤害的异步序列（模拟玩家连击致死）。
        /// 攻击者 = 本地玩家（保证击杀归属正确）；最后一次扣剩余 HP 触发 <c>EnterDead</c> → <c>DiedAnimTimer</c>。
        /// </summary>
        private async UniTaskVoid DebugKillSequenceAsync(System.Threading.CancellationToken ct)
        {
            if (_model == null || _model.IsDead()) return;

            EntityId attackerId = Svcer.Req<EntityId>(SvcID.QueryLocalPlayer);

            int maxHp = _model.MaxHp;
            int perHit = Mathf.Max(1, Mathf.CeilToInt(maxHp / (float)DEBUG_KILL_HIT_COUNT) + 1);

            Logging.Info(
                $"[MonsterCtrl.DebugKill] 模拟本地玩家连击致死 id={Id} cfgId={_model.Template.CfgId} "
                + $"MaxHp={maxHp} → 分 {DEBUG_KILL_HIT_COUNT} 次，每次 {perHit} 伤害"
            );

            for (int i = 0; i < DEBUG_KILL_HIT_COUNT; i++)
            {
                if (ct.IsCancellationRequested) return;
                if (_model == null || _model.IsDead()) return;

                // 最后一段：直接扣剩余 HP，避免 overkill
                int damage = (i == DEBUG_KILL_HIT_COUNT - 1) ? Mathf.Max(1, _model.Hp) : perHit;

                RequestApplyDamage(new DamageData { Damage = damage, AttackerId = attackerId, });

                await UniTask.Delay(TimeSpan.FromSeconds(DEBUG_KILL_HIT_INTERVAL), cancellationToken: ct);
            }
        }

        #endregion

#endif
    }
}
