using System;
using UnityEngine;
using Unity.InferenceEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Runs an ONNX locomotion policy through Unity Inference Engine (formerly Sentis)
    /// and drives a CreatureRig with PD position targets.
    ///
    /// - Input and output buffers are allocated once in Initialize(); FixedUpdate is allocation-free
    ///   when the backend is CPU (output is read through a ReadOnlySpan).
    /// - Policy runs every `controlDecimation` physics steps (200 Hz / 4 = 50 Hz by default).
    /// - When no model is assigned the rig simply holds its default pose so the scene still runs.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PolicyRunner : MonoBehaviour
    {
        public CreatureRig rig;
        public PolicyConfig config;
        public ModelAsset model;
        public BackendType backend = BackendType.CPU;
        public VelocityCommandSource commandSource;
        [Tooltip("Layer used by the creature so height-scan raycasts ignore it.")]
        public string creatureLayerName = "Creature";

        Model _model;
        Worker _worker;
        Tensor<float> _input;
        ObservationBuilder _obsBuilder;

        float[] _obs = new float[0];
        float[] _action = new float[0];
        float[] _lastAction = new float[0];
        float[] _targets = new float[0];
        float[] _jointPos = new float[0];
        float[] _jointVel = new float[0];

        int _stepCounter;
        bool _ready;
        bool _warnedShape;

        public bool IsReady => _ready;
        public bool HasModel => _worker != null;
        public int ObservationSize => _obs.Length;
        public int ActionSize => _action.Length;
        public float[] LastObservation => _obs;
        public float[] LastAction => _lastAction;
        public string ModelName => model != null ? model.name : "(none: holding default pose)";
        public int PolicySteps { get; private set; }

        public void Initialize(PolicyConfig cfg, ModelAsset asset)
        {
            DisposeWorker();
            config = cfg;
            model = asset;

            if (config == null) { Debug.LogError("[PolicyRunner] No PolicyConfig.", this); return; }
            if (rig == null) rig = GetComponentInChildren<CreatureRig>();
            if (rig == null) { Debug.LogError("[PolicyRunner] No CreatureRig.", this); return; }

            Time.fixedDeltaTime = config.FixedDeltaTime;
            rig.Bind(config);

            int obsDim = config.ObservationSize;
            int actDim = config.ActionSize;
            _obs = new float[obsDim];
            _action = new float[actDim];
            _lastAction = new float[actDim];
            _targets = new float[actDim];
            _jointPos = new float[actDim];
            _jointVel = new float[actDim];
            _obsBuilder = new ObservationBuilder(config, LayerMask.NameToLayer(creatureLayerName));

            if (asset != null)
            {
                try
                {
                    _model = ModelLoader.Load(asset);
                    ValidateShapes(obsDim, actDim);
                    _worker = new Worker(_model, backend);
                    _input = new Tensor<float>(new TensorShape(1, obsDim), false);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[PolicyRunner] Failed to load '{asset.name}': {e.Message}", this);
                    DisposeWorker();
                }
            }

            for (int i = 0; i < actDim; i++) _targets[i] = rig.DefaultPosition(i);
            rig.ApplyTargets(_targets);
            _stepCounter = 0;
            PolicySteps = 0;
            _ready = true;
        }

        void ValidateShapes(int obsDim, int actDim)
        {
            if (_model.inputs.Count == 0) throw new InvalidOperationException("model has no inputs");
            var inShape = _model.inputs[0].shape;
            if (inShape.IsStatic())
            {
                TensorShape s = inShape.ToTensorShape();
                int last = s[s.rank - 1];
                if (last != obsDim)
                    throw new InvalidOperationException($"model expects {last} observations but PolicyConfig produces {obsDim}");
            }
            else if (!_warnedShape)
            {
                _warnedShape = true;
                Debug.LogWarning($"[PolicyRunner] Model input '{_model.inputs[0].name}' has a dynamic shape; assuming (1, {obsDim}).", this);
            }
        }

        void FixedUpdate()
        {
            if (!_ready) return;
            // Editor hot-reload keeps serialized fields but drops plain C# objects; rebuild them instead of throwing.
            if (_obsBuilder == null || (model != null && _worker == null))
            {
                Debug.LogWarning("[PolicyRunner] Runtime objects lost (domain reload); re-initializing.", this);
                Initialize(config, model);
                return;
            }
            _stepCounter++;
            if (_stepCounter % Mathf.Max(1, config.controlDecimation) != 0) return;
            Step();
        }

        void Step()
        {
            rig.ReadJointState(_jointPos, _jointVel);

            Vector3 cmd = Vector3.zero;
            if (commandSource != null)
            {
                commandSource.Tick(rig);
                cmd = commandSource.Command;
            }

            _obsBuilder.Fill(_obs, rig, cmd, _lastAction, _jointPos, _jointVel);

            int n = _action.Length;
            if (_worker != null)
            {
                _input.Upload(_obs);
                _worker.Schedule(_input);
                var output = _worker.PeekOutput() as Tensor<float>;
                if (output != null)
                {
                    output.CompleteAllPendingOperations();
                    ReadOnlySpan<float> span = output.AsReadOnlySpan();
                    int count = Mathf.Min(span.Length, n);
                    float clip = config.actionClip;
                    for (int i = 0; i < count; i++)
                    {
                        float a = span[i];
                        if (float.IsNaN(a) || float.IsInfinity(a)) a = 0f;
                        _action[i] = Mathf.Clamp(a, -clip, clip);
                    }
                    for (int i = count; i < n; i++) _action[i] = 0f;
                }
            }
            else
            {
                for (int i = 0; i < n; i++) _action[i] = 0f;
            }

            float scale = config.actionScale;
            for (int i = 0; i < n; i++)
            {
                _targets[i] = rig.DefaultPosition(i) + _action[i] * scale;
                _lastAction[i] = _action[i];
            }
            rig.ApplyTargets(_targets);
            PolicySteps++;
        }

        public void ResetEpisode(Vector3 position, Quaternion rotation)
        {
            Array.Clear(_lastAction, 0, _lastAction.Length);
            Array.Clear(_action, 0, _action.Length);
            for (int i = 0; i < _targets.Length; i++) _targets[i] = rig.DefaultPosition(i);
            rig.ResetPose(position, rotation);
            _stepCounter = 0;
        }

        void DisposeWorker()
        {
            _input?.Dispose();
            _worker?.Dispose();
            _input = null;
            _worker = null;
            _model = null;
        }

        void OnDestroy() => DisposeWorker();
    }
}
