using System;
using cfg;
using Framework;
using Game.Components;
using TMPro;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterRenderModuleConfig
    {
        [Tooltip("渲染组件")]
        public MonsterRendering Rendering;

        [Tooltip("头顶名字组件")]
        public TextMeshProUGUI NameUI;
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

            _model.OnTemplateChanged += OnTemplateChangedHandler;
            _model.OnAnimIdChanged += OnAnimIdChangedHandler;
            _model.OnHpChanged += OnHpChangedHandler;
            _model.OnMaxHpChanged += OnMaxHpChangedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnTemplateChanged -= OnTemplateChangedHandler;
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

        private void OnTemplateChangedHandler(MonsterTemplate template)
        {
            if (_model is null) return;
            if (_config is null) return;
            if (_config.NameUI == null) return;

            var cfg = _model.Cfg;
            if (cfg == null) return;

            switch (cfg.MonsterType)
            {
                case MonsterType.Small:
                    _config.NameUI.gameObject.SetActive(false);
                    return;
                case MonsterType.Elite:
                    _config.NameUI.text = $"{MonsterType.Elite}-{cfg.WeaponName}";
                    _config.NameUI.gameObject.SetActive(true);
                    return;
                case MonsterType.Boss:
                    _config.NameUI.text = $"{MonsterType.Boss}-{cfg.WeaponName}";
                    _config.NameUI.gameObject.SetActive(true);
                    return;
                default:
                    _config.NameUI.gameObject.SetActive(false);
                    return;
            }
        }

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

            if (_config.Rendering != null)
            {
                // 全端表现类 handler：不加权威检查
                _config.Rendering.UpdateHp(hp, _model.MaxHp);
            }

            if (hp <= 0)
            {
                if (_config.NameUI != null) _config.NameUI.gameObject.SetActive(false);
            }
        }

        private void OnMaxHpChangedHandler(int maxHp)
        {
            if (_config is null) return;
            if (_config.Rendering == null) return;

            // 全端表现类 handler：不加权威检查
            _config.Rendering.UpdateHp(_model.Hp, maxHp);
        }

        #endregion

        #region Component Handlers

        #endregion

        #region Private Methods

        #endregion

        #region Helpers

        private static string BuildDisplayName(string prefix, string weaponName)
        {
            return $"{prefix}-{weaponName ?? string.Empty}";
        }

        #endregion
    }
}
