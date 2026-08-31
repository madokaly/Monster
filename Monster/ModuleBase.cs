namespace Framework
{
    public abstract class ModuleBase
    {
        public bool HasStateAuthority { get; protected set; } = false;

        public void Dispose()
        {
            OnDispose();
        }

        public void StateAuthorityChanged(bool hasStateAuthority)
        {
            HasStateAuthority = hasStateAuthority;
            OnStateAuthorityChanged();
        }

        public void FixedUpdate(float fixedDeltaTime)
        {
            OnFixedUpdate(fixedDeltaTime);
        }

        public void FixedUpdateNetwork(float deltaTime)
        {
            OnFixedUpdateNetwork(deltaTime);
        }

        public void Update(float deltaTime)
        {
            OnUpdate(deltaTime);
        }

        public void LateUpdate(float deltaTime)
        {
            OnLateUpdate(deltaTime);
        }

        protected virtual void OnDispose() { }

        protected virtual void OnStateAuthorityChanged() { }

        protected virtual void OnFixedUpdate(float fixedDeltaTime) { }

        protected virtual void OnFixedUpdateNetwork(float deltaTime) { }

        protected virtual void OnUpdate(float deltaTime) { }

        protected virtual void OnLateUpdate(float deltaTime) { }
    }
}
