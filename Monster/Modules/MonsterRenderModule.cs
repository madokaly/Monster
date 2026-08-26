using System;
using Framework;
using Game.Components;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterRenderModuleConfig
    {
        [Tooltip("渲染组件")]
        public MonsterRendering Rendering;
    }

    /// <summary>
    /// 怪物渲染模块：监听 Model 动画 / 血量事实，被动刷新渲染组件（全端执行）。
    /// </summary>
    public class MonsterRenderModule : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterRenderModuleConfig _config;

        #region Lifecycle

        public MonsterRenderModule(MonsterModel model, MonsterRenderModuleConfig config)
        {
            _model = model;
            _config = config;

            // 注册 Model / Components 的事件监听
            RegisterModelListeners();
            RegisterComponentListeners();
        }

        protected override void OnDispose()
        {
            // 清空 Model / Components 的事件监听
            ClearModelListeners();
            ClearComponentListeners();
        }

        protected override void OnStateAuthorityChanged() { }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;

            _model.OnAnimIdChanged += OnAnimIdChangedHandler;
            _model.OnHpChanged += OnHpChangedHandler;
            _model.OnMaxHpChanged += OnMaxHpChangedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnAnimIdChanged -= OnAnimIdChangedHandler;
            _model.OnHpChanged -= OnHpChangedHandler;
            _model.OnMaxHpChanged -= OnMaxHpChangedHandler;
        }

        private void RegisterComponentListeners()
        {
            if (_config is null) return;
        }

        private void ClearComponentListeners()
        {
            if (_config is null) return;
        }

        #endregion

        #region Model Handlers

        private void OnAnimIdChangedHandler(int animId)
        {
            if (_config is null) return;
            if (_config.Rendering == null) return;

            // 全端表现类 handler：不加权威检查
            _config.Rendering.SetAnimId(animId);
        }

        private void OnHpChangedHandler(int hp)
        {
            if (_config is null) return;
            if (_config.Rendering == null) return;

            // 全端表现类 handler：不加权威检查
            _config.Rendering.UpdateHp(hp, _model.MaxHp);
        }

        private void OnMaxHpChangedHandler(int maxHp)
        {
            if (_config is null) return;
            if (_config.Rendering == null) return;

            // 全端表现类 handler：不加权威检查
            _config.Rendering.UpdateHp(_model.Hp, maxHp);
        }

        #endregion
    }
}
