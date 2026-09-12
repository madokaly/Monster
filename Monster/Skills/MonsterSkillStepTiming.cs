using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// Bombing 各阶段关键时刻的单一计算入口。
    /// </summary>
    public static class MonsterBombingTiming
    {
        public static float GetLockTime(MonsterBombingStepConfig config)
        {
            return config != null ? config.TrackDuration : 0f;
        }

        public static float GetBombSpawnTime(MonsterBombingStepConfig config, int waveIndex)
        {
            if (config == null) return 0f;

            return config.TrackDuration + config.LockDelay + waveIndex * config.BombInterval;
        }

        public static float GetBombSettleTime(MonsterBombingStepConfig config, int waveIndex)
        {
            if (config == null) return 0f;

            return GetBombSpawnTime(config, waveIndex) + config.BombFallDuration;
        }
    }

    /// <summary>
    /// Chase 窗口进度、固定落点与位移的单一计算入口。
    /// </summary>
    public static class MonsterChaseTiming
    {
        public static float GetProgress(MonsterChaseStepConfig config, float stepElapsed)
        {
            if (config == null || config.ChaseWindow <= 0f) return 1f;
            return Mathf.Clamp01(stepElapsed / config.ChaseWindow);
        }

        public static bool TryEvaluateDestination(
            MonsterChaseStepConfig config,
            Vector3 startPosition,
            Vector3 targetPosition,
            out Vector3 destination)
        {
            destination = startPosition;
            if (config == null) return false;

            Vector3 toTarget = targetPosition - startPosition;
            toTarget.y = 0f;
            float startDistance = toTarget.magnitude;
            if (startDistance < 0.001f || startDistance <= config.ChaseStartDistance) return false;

            Vector3 direction = toTarget / startDistance;
            if (startDistance <= config.ChaseStartDistance + config.ChaseDistance)
            {
                destination = new Vector3(
                    targetPosition.x - direction.x * config.ChaseStartDistance,
                    startPosition.y,
                    targetPosition.z - direction.z * config.ChaseStartDistance
                );
            }
            else
            {
                destination = new Vector3(
                    startPosition.x + direction.x * config.ChaseDistance,
                    startPosition.y,
                    startPosition.z + direction.z * config.ChaseDistance
                );
            }

            return true;
        }

        public static Vector3 EvaluatePosition(
            Vector3 startPosition,
            Vector3 destination,
            bool willMove,
            float progress)
        {
            return willMove
                ? Vector3.Lerp(startPosition, destination, Mathf.Clamp01(progress))
                : startPosition;
        }
    }

    /// <summary>
    /// Aim 的平面转向单一计算入口（运行时转向与 Editor 预演共用）。
    /// </summary>
    public static class MonsterAimTiming
    {
        public static Quaternion RotateTowardsPlanarTarget(
            Quaternion rotation,
            Vector3 targetDirection,
            float maxDegrees)
        {
            Vector3 direction = targetDirection;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f) return rotation;

            return Quaternion.RotateTowards(
                rotation,
                Quaternion.LookRotation(direction),
                Mathf.Max(0f, maxDegrees)
            );
        }
    }

    /// <summary>
    /// InstantShot 结算时刻的单一计算入口。
    /// </summary>
    public static class MonsterInstantShotTiming
    {
        public static float GetDamageTime(MonsterInstantShotStepConfig config)
        {
            return config != null ? Mathf.Max(0f, config.DamageOffset) : 0f;
        }
    }

    /// <summary>
    /// 直线弹道发射与飞行的单一计算入口。
    /// </summary>
    public static class MonsterProjectileTiming
    {
        public static float GetSpeed(MonsterProjectileStepConfig config)
        {
            return config != null && config.ProjectileSpeed > 0f ? config.ProjectileSpeed : 20f;
        }

        public static float GetMaxDistance(MonsterProjectileStepConfig config)
        {
            return config != null && config.MaxDistance > 0f ? config.MaxDistance : 50f;
        }

        public static float GetFlightDuration(MonsterProjectileStepConfig config)
        {
            return GetMaxDistance(config) / Mathf.Max(0.001f, GetSpeed(config));
        }

        public static Vector3 GetSpawnPosition(MonsterProjectileStepConfig config)
        {
            if (config?.SelfTransform == null) return default;
            return config.SelfTransform.position + config.SelfTransform.rotation * config.SpawnOffset;
        }

        /// <summary>
        /// 解析水平发射方向：目标锁定忽略 Y 偏移，弹道保持发射点高度直线飞行。
        /// </summary>
        public static Vector3 ResolveDirection(
            MonsterProjectileStepConfig config,
            Vector3 spawnPosition,
            bool hasTarget,
            Vector3 targetPosition)
        {
            Vector3 fallback = config?.SelfTransform != null
                ? config.SelfTransform.forward
                : Vector3.forward;
            fallback.y = 0f;
            if (fallback.sqrMagnitude < 0.001f) fallback = Vector3.forward;

            Vector3 direction = hasTarget ? targetPosition - spawnPosition : fallback;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) direction = fallback;
            return direction.sqrMagnitude > 0f ? direction.normalized : Vector3.forward;
        }

        public static Vector3 EvaluatePosition(
            MonsterProjectileStepConfig config,
            Vector3 spawnPosition,
            Vector3 direction,
            float flightAge)
        {
            float distance = Mathf.Min(
                Mathf.Max(0f, flightAge) * GetSpeed(config),
                GetMaxDistance(config)
            );
            return spawnPosition + direction * distance;
        }

        public static float GetHitRadius(MonsterProjectileStepConfig config)
        {
            return config != null ? Mathf.Max(0f, config.HitRadius) : 0f;
        }

        public static float GetHitHeight(MonsterProjectileStepConfig config)
        {
            float radius = GetHitRadius(config);
            float height = config != null ? Mathf.Max(0f, config.HitHeight) : 0f;
            return Mathf.Max(height, radius * 2f);
        }

        public static void GetHitCapsuleAxisPoints(
            MonsterProjectileStepConfig config,
            Vector3 projectilePosition,
            Quaternion projectileRotation,
            out Vector3 lowerPoint,
            out Vector3 upperPoint)
        {
            GetHitCapsuleAxisPoints(
                GetHitRadius(config),
                GetHitHeight(config),
                projectilePosition,
                projectileRotation,
                config?.HitCenterOffset ?? Vector3.zero,
                out lowerPoint,
                out upperPoint
            );
        }

        public static void GetHitCapsuleAxisPoints(
            float hitRadius,
            float hitHeight,
            Vector3 projectilePosition,
            Quaternion projectileRotation,
            Vector3 hitCenterOffset,
            out Vector3 lowerPoint,
            out Vector3 upperPoint)
        {
            Vector3 hitCenter = projectilePosition + projectileRotation * hitCenterOffset;
            hitHeight = Mathf.Max(hitHeight, hitRadius * 2f);
            Vector3 halfAxis = Vector3.up * ((hitHeight - hitRadius * 2f) * 0.5f);
            lowerPoint = hitCenter - halfAxis;
            upperPoint = hitCenter + halfAxis;
        }
    }

    /// <summary>
    /// WaterBall 汇聚、抛射与落地的单一计算入口（运行时与 Editor 预演共用）。
    /// </summary>
    public static class MonsterWaterBallTiming
    {
        public const string FIRE_SMOKE_CHILD_NAME = "FireSmkA";
        public const string WATER_BALL_CHILD_NAME = "water_ball";

        public static float GetChargeDuration(MonsterWaterBallStepConfig config)
        {
            return config != null ? Mathf.Max(0f, config.ChargeDuration) : 0f;
        }

        public static float GetThrowDuration(MonsterWaterBallStepConfig config)
        {
            return config != null ? Mathf.Max(0f, config.ThrowDuration) : 0f;
        }

        public static float GetLandingTime(MonsterWaterBallStepConfig config)
        {
            return GetChargeDuration(config) + GetThrowDuration(config);
        }

        public static float GetGrowthProgress(MonsterWaterBallStepConfig config, float stepElapsed)
        {
            float duration = GetChargeDuration(config);
            if (duration <= 0f) return 1f;

            return Mathf.Clamp01(Mathf.Max(0f, stepElapsed) / duration);
        }

        public static float GetGrowthScale(MonsterWaterBallStepConfig config, float stepElapsed)
        {
            float progress = GetGrowthProgress(config, stepElapsed);
            float easedProgress = Mathf.SmoothStep(0f, 1f, progress);
            float multiplier = config != null ? Mathf.Max(0f, config.FinalScaleMultiplier) : 1f;
            return easedProgress * multiplier;
        }

        public static float GetThrowProgress(MonsterWaterBallStepConfig config, float stepElapsed)
        {
            float duration = GetThrowDuration(config);
            if (duration <= 0f) return 1f;

            return Mathf.Clamp01((stepElapsed - GetChargeDuration(config)) / duration);
        }

        public static Vector3 GetSpawnPosition(MonsterWaterBallStepConfig config, Transform body)
        {
            if (body == null) return Vector3.zero;

            Vector3 offset = config != null ? config.SpawnOffset : Vector3.zero;
            return body.TransformPoint(offset);
        }

        public static Vector3 ResolveLandingPosition(
            MonsterWaterBallStepConfig config,
            Vector3 bodyPosition,
            Vector3 bodyForward,
            bool hasTarget,
            Vector3 targetPosition)
        {
            if (hasTarget) return targetPosition;

            Vector3 forward = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
            if (forward.sqrMagnitude <= 0.0001f) forward = Vector3.forward;

            float distance = config != null ? Mathf.Max(0f, config.FallbackThrowDistance) : 0f;
            return bodyPosition + forward.normalized * distance;
        }

        public static Vector3 EvaluateThrowPosition(
            MonsterWaterBallStepConfig config,
            Vector3 startPosition,
            Vector3 landingPosition,
            float stepElapsed)
        {
            float progress = GetThrowProgress(config, stepElapsed);
            float arcHeight = config != null ? Mathf.Max(0f, config.ArcHeight) : 0f;
            return Vector3.Lerp(startPosition, landingPosition, progress)
                   + Vector3.up * (arcHeight * 4f * progress * (1f - progress));
        }
    }

    /// <summary>
    /// Beam 阶段边界与方向的单一计算入口。
    /// </summary>
    public static class MonsterBeamTiming
    {
        public static float GetBeamStartTime(MonsterBeamStepConfig config)
        {
            return config != null ? config.ChargeDuration : 0f;
        }

        public static float GetBeamEndTime(MonsterBeamStepConfig config)
        {
            return config != null ? config.ChargeDuration + config.BeamDuration : 0f;
        }

        public static bool IsDamageActive(MonsterBeamStepConfig config, float stepElapsed)
        {
            return config != null
                   && stepElapsed >= GetBeamStartTime(config)
                   && stepElapsed < GetBeamEndTime(config);
        }

        /// <summary>
        /// 光束方向（水平锁定 / 垂直自由）：水平向 = yawForward 水平投影
        /// （身体前向锁定——水平跟踪只由身体转动承担），俯仰 = 有目标时按
        /// Δy / 水平距离即时瞄准（水平距趋 0 钳 ±90°），无目标 pitch 0。
        /// </summary>
        public static Vector3 ResolveDirection(
            Vector3 origin,
            Vector3 yawForward,
            bool hasTarget,
            Vector3 targetPosition)
        {
            Vector3 horizontal = yawForward;
            horizontal.y = 0f;
            if (horizontal.sqrMagnitude < 0.001f) horizontal = Vector3.forward;
            horizontal.Normalize();

            if (!hasTarget) return horizontal;

            float heightDelta = targetPosition.y - origin.y;
            Vector3 toTarget = targetPosition - origin;
            toTarget.y = 0f;
            float horizontalDistance = toTarget.magnitude;

            float pitchRad = horizontalDistance < 0.001f
                ? Mathf.PI * 0.5f * (heightDelta < 0f ? -1f : 1f)
                : Mathf.Atan2(heightDelta, horizontalDistance);

            float pitchCos = Mathf.Cos(pitchRad);
            return new Vector3(
                horizontal.x * pitchCos,
                Mathf.Sin(pitchRad),
                horizontal.z * pitchCos
            );
        }
    }

    /// <summary>
    /// Melee 伤害窗口边界的单一计算入口。
    /// </summary>
    public static class MonsterMeleeTiming
    {
        public static float GetHitStartTime(MonsterMeleeStepConfig config)
        {
            return config != null ? Mathf.Max(0f, config.HitDelay) : 0f;
        }

        public static float GetHitEndTime(MonsterMeleeStepConfig config)
        {
            return config != null
                ? GetHitStartTime(config) + Mathf.Max(0f, config.Window)
                : 0f;
        }

        public static bool IsDamageActive(MonsterMeleeStepConfig config, float stepElapsed)
        {
            return config != null
                   && config.Window > 0f
                   && stepElapsed >= GetHitStartTime(config)
                   && stepElapsed < GetHitEndTime(config);
        }
    }

    /// <summary>
    /// Burrow 阶段边界的单一计算入口。
    /// 追踪位移计算（MovePlanarTowards / EstimateEmergeTimeForStationaryPointTarget）
    /// 为 Editor 预演的直线近似——运行时追踪已改寻路跟随（预演禁寻路，§18.6.2）。
    /// </summary>
    public static class MonsterBurrowTiming
    {
        public static float GetTrackStartTime(MonsterBurrowStepConfig config)
        {
            return config != null ? config.DiveDuration + config.StartTrackDelay : 0f;
        }

        public static float GetForcedEmergeTime(MonsterBurrowStepConfig config)
        {
            return config != null
                ? GetTrackStartTime(config) + config.MaxTrackDuration
                : 0f;
        }

        public static Vector3 MovePlanarTowards(
            Vector3 currentPosition,
            Vector3 targetPosition,
            float maxDistanceDelta,
            out float movedDistance)
        {
            Vector3 toTarget = targetPosition - currentPosition;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 0.001f)
            {
                movedDistance = 0f;
                return currentPosition;
            }

            Vector3 move = toTarget.normalized * Mathf.Min(
                Mathf.Max(0f, maxDistanceDelta),
                toTarget.magnitude
            );
            movedDistance = move.magnitude;
            return currentPosition + move;
        }

        public static float EstimateEmergeTimeForStationaryPointTarget(
            MonsterBurrowStepConfig config,
            Vector3 startPosition,
            Vector3 targetPosition)
        {
            float forcedTime = GetForcedEmergeTime(config);
            if (config == null || !config.EndOnHitTrackedTarget || config.TrackSpeed <= 0f)
            {
                return forcedTime;
            }

            Vector3 toTarget = targetPosition - startPosition;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            float requiredMove = Mathf.Max(
                config.MinMoveDistanceForHit,
                Mathf.Max(0f, distance - config.TriggerHitRadius)
            );
            if (requiredMove > distance) return forcedTime;

            return Mathf.Min(
                forcedTime,
                GetTrackStartTime(config) + requiredMove / config.TrackSpeed
            );
        }
    }

    /// <summary>
    /// Rockfall 陨石下落物理与各关键时刻的单一计算入口。
    /// 下落为纯运动学（初速 + 匀加速 + 速度上限），运行时表现与落地结算时刻共用本计算。
    /// </summary>
    public static class MonsterRockfallTiming
    {
        /// <summary>
        /// 陨石从起始高度落到落点的时长（初速 + 匀加速 + 上限截断的解析解）。
        /// </summary>
        public static float GetFallDuration(MonsterRockfallStepConfig config)
        {
            if (config == null || config.RockFallHeight <= 0f) return 0f;

            float initialSpeed = Mathf.Max(0f, config.RockFallInitialSpeed);
            float gravity = Mathf.Max(0.001f, config.RockFallGravity);
            float maxSpeed = Mathf.Max(0.001f, config.RockMaxFallSpeed);

            if (initialSpeed >= maxSpeed)
            {
                // 初速即达上限：全程匀速
                return config.RockFallHeight / maxSpeed;
            }

            // 加速段距离 = (vMax² - v0²) / (2g)
            float accelDistance = (maxSpeed * maxSpeed - initialSpeed * initialSpeed) / (2f * gravity);
            if (accelDistance >= config.RockFallHeight)
            {
                // 全程加速：h = v0·t + g·t²/2 → t = (√(v0² + 2gh) - v0) / g
                return (Mathf.Sqrt(initialSpeed * initialSpeed + 2f * gravity * config.RockFallHeight) - initialSpeed)
                       / gravity;
            }

            // 加速段 + 匀速段
            float accelTime = (maxSpeed - initialSpeed) / gravity;
            return accelTime + (config.RockFallHeight - accelDistance) / maxSpeed;
        }

        /// <summary> 第 rockIndex 块陨石开始下落的时刻（步骤起点起算） </summary>
        public static float GetRockFallStartTime(MonsterRockfallStepConfig config, int rockIndex)
        {
            if (config?.RockFallDelays is not { Length: > 0 }) return 0f;
            return config.RockFallDelays[Mathf.Clamp(rockIndex, 0, config.RockFallDelays.Length - 1)];
        }

        /// <summary>
        /// 下落开始 fallAge 秒后石体距落点的高度（与 GetFallDuration 同一套运动学解析；
        /// 运行时表现与 Editor 预演共用，§18.6.2）。
        /// </summary>
        public static float GetFallHeightAt(
            float fallAge,
            float initialSpeed,
            float gravity,
            float maxSpeed,
            float height)
        {
            fallAge = Mathf.Max(0f, fallAge);
            initialSpeed = Mathf.Max(0f, initialSpeed);
            gravity = Mathf.Max(0.001f, gravity);
            maxSpeed = Mathf.Max(0.001f, maxSpeed);

            float distance;
            if (initialSpeed >= maxSpeed)
            {
                // 初速即达上限：全程匀速
                distance = fallAge * maxSpeed;
            }
            else
            {
                float accelTime = (maxSpeed - initialSpeed) / gravity;
                float accelDistance = initialSpeed * accelTime + 0.5f * gravity * accelTime * accelTime;

                distance = fallAge <= accelTime
                    ? initialSpeed * fallAge + 0.5f * gravity * fallAge * fallAge
                    : accelDistance + (fallAge - accelTime) * maxSpeed;
            }

            return Mathf.Max(0f, height - distance);
        }

        /// <summary> 第 rockIndex 块陨石落地时刻（步骤起点起算；权威端结算基准） </summary>
        public static float GetRockLandTime(MonsterRockfallStepConfig config, int rockIndex)
        {
            return GetRockFallStartTime(config, rockIndex) + GetFallDuration(config);
        }

        /// <summary> 内圈持续 AOE 活跃区间 [InnerStartDelay, InnerStartDelay + InnerDuration) </summary>
        public static bool IsInnerAoeActive(MonsterRockfallStepConfig config, float stepElapsed)
        {
            return config != null
                   && stepElapsed >= config.InnerStartDelay
                   && stepElapsed < config.InnerStartDelay + Mathf.Max(0f, config.InnerDuration);
        }

        /// <summary> 末块陨石落地时刻（Duration 派生 / 链排布基准） </summary>
        public static float GetLastRockLandTime(MonsterRockfallStepConfig config)
        {
            if (config == null) return 0f;

            float last = 0f;
            for (int i = 0; i < config.RockCount; i++)
            {
                last = Mathf.Max(last, GetRockLandTime(config, i));
            }
            return last;
        }
    }
    /// <summary>
    /// Charge 的锁定方向、阶段时刻与位移的单一计算入口（运行时与 Editor 预演共用）。
    /// </summary>
    public static class MonsterChargeTiming
    {
        /// <summary> 蓄力 / 预警结束（冲撞开始）时刻，步骤起点起算 </summary>
        public static float GetWarningEndTime(MonsterChargeCrashStepConfig config)
        {
            return config != null ? Mathf.Max(0f, config.WarningDuration) : 0f;
        }

        /// <summary> 冲撞进度（0-1；冲撞时长非法时保持 1，供装配校验前预演兜底） </summary>
        public static float GetChargeProgress(MonsterChargeCrashStepConfig config, float stepElapsed)
        {
            if (config == null || config.ChargeDuration <= 0f) return 1f;

            return Mathf.Clamp01((stepElapsed - GetWarningEndTime(config)) / config.ChargeDuration);
        }

        /// <summary>
        /// 解析水平锁定方向。目标与起点重合时返回 false，由调用方选择本体朝向等兜底方向。
        /// </summary>
        public static bool TryResolvePlanarDirection(Vector3 origin, Vector3 target, out Vector3 direction)
        {
            Vector3 toTarget = target - origin;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 0.0001f)
            {
                direction = Vector3.forward;
                return false;
            }

            direction = toTarget.normalized;
            return true;
        }

        /// <summary> 按冲撞进度求固定距离直线位移 </summary>
        public static Vector3 EvaluatePosition(
            Vector3 startPosition,
            Vector3 direction,
            float distance,
            float progress)
        {
            return startPosition + direction * Mathf.Max(0f, distance) * Mathf.Clamp01(progress);
        }

        /// <summary>
        /// 解析撞伤身体胶囊的竖直轴两端球心（总高度 = 轴长度 + 2 × 半径；
        /// 运行时扫掠判定与 Editor 预演共用，保证判定几何一致）。
        /// </summary>
        public static void GetContactCapsuleAxisPoints(
            MonsterChargeCrashStepConfig config,
            Vector3 bodyPosition,
            out Vector3 lowerPoint,
            out Vector3 upperPoint)
        {
            float radius = config != null ? Mathf.Max(0f, config.ContactHitRadius) : 0f;
            float height = config != null && config.ContactHitHeight > 0f
                ? config.ContactHitHeight
                : radius * 2f;
            height = Mathf.Max(height, radius * 2f);

            float centerHeight = config != null ? config.ContactHitCenterHeight : 0f;
            Vector3 center = bodyPosition + Vector3.up * centerHeight;
            Vector3 halfAxis = Vector3.up * ((height - radius * 2f) * 0.5f);
            lowerPoint = center - halfAxis;
            upperPoint = center + halfAxis;
        }
    }

    /// <summary>
    /// Entrance 的下落进度与位移单一计算入口（运行时与 Editor 预演共用）。
    /// </summary>
    public static class MonsterEntranceTiming
    {
        public const float WARNING_SURFACE_HEIGHT_OFFSET = 0.2f;

        /// <summary> 下落进度（0-1；时长非法时保持 1，供装配校验前预演兜底） </summary>
        public static float GetDescendProgress(
            MonsterEntranceDescendStepConfig config,
            float stepElapsed)
        {
            if (config == null || config.DescendDuration <= 0f) return 1f;

            return Mathf.Clamp01(Mathf.Max(0f, stepElapsed) / config.DescendDuration);
        }

        /// <summary> Ease-in 下落进度 </summary>
        public static float GetEasedDescendProgress(
            MonsterEntranceDescendStepConfig config,
            float stepElapsed)
        {
            float progress = GetDescendProgress(config, stepElapsed);
            return progress * progress;
        }

        /// <summary> 按下落进度求视觉根世界位置（网络根保持最终落点） </summary>
        public static Vector3 EvaluateDescendPosition(
            Vector3 originalPosition,
            MonsterEntranceDescendStepConfig config,
            float stepElapsed)
        {
            if (config == null) return originalPosition;

            float remainHeight =
                Mathf.Max(0f, config.DescendHeight)
                * (1f - GetEasedDescendProgress(config, stepElapsed));

            return originalPosition + Vector3.up * remainHeight;
        }

        /// <summary> 命中地面上方的固定表现抬升（避免 warning 与地面 / 深度面穿插） </summary>
        public static Vector3 GetWarningSurfacePosition(Vector3 surfacePosition)
        {
            return surfacePosition + Vector3.up * WARNING_SURFACE_HEIGHT_OFFSET;
        }

        /// <summary>
        /// 落点预警保持水平姿态：只继承网络根 yaw，不继承 pitch / roll，也不对齐地面法线。
        /// </summary>
        public static Quaternion GetWarningYawRotation(Transform selfTransform)
        {
            return Quaternion.Euler(0f, selfTransform.eulerAngles.y, 0f);
        }

        /// <summary> 落点预警最终朝向（yaw-only 根朝向 + 配置的特效局部旋转） </summary>
        public static Quaternion GetWarningRotation(
            Transform selfTransform,
            MonsterEffectSettings warningEffect)
        {
            return GetWarningYawRotation(selfTransform) * Quaternion.Euler(warningEffect.Rotation);
        }

        /// <summary> 落点预警局部偏移（按 yaw-only 旋转与根缩放换算到世界空间） </summary>
        public static Vector3 GetWarningOffset(
            Transform selfTransform,
            MonsterEffectSettings warningEffect)
        {
            return GetWarningYawRotation(selfTransform)
                   * Vector3.Scale(selfTransform.lossyScale, warningEffect.Offset);
        }

        /// <summary> 落点预警等效世界缩放（保留挂父级时的 lossy scale 语义） </summary>
        public static Vector3 GetWarningScale(
            Transform selfTransform,
            MonsterEffectSettings warningEffect)
        {
            return Vector3.Scale(selfTransform.lossyScale, warningEffect.Scale);
        }
    }
}
