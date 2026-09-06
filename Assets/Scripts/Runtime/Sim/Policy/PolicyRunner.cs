using System;
using UnityEngine;
using Unity.InferenceEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Runs an ONNX locomotion policy through Unity Inference Engine (formerly Sentis)
    /// and drives a AthleteRig with PD position targets.
    ///
    /// - Input and output buffers are allocated once in Initialize(); FixedUpdate is allocation-free
    ///   when the backend is CPU (output is read through a ReadOnlySpan).
    /// - Policy runs every `controlDecimation` physics steps (200 Hz / 4 = 50 Hz by default).
    /// - When no model is assigned the rig simply holds its default pose so the scene still runs.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PolicyRunner : MonoBehaviour
    {
        public AthleteRig rig;
        public PolicyConfig config;
        public ModelAsset model;
        [Tooltip("Optional second policy for getting up off the ground (athlete_getup.onnx). Loaded "
               + "alongside the main one and held ready, so switching to it costs nothing at the moment a "
               + "runner goes down — which is the one moment in a race that must not hitch.")]
        public ModelAsset recoveryModel;
        public BackendType backend = BackendType.CPU;
        public VelocityCommandSource commandSource;
        [Tooltip("Layer used by the creature so height-scan raycasts ignore it.")]
        public string creatureLayerName = "Creature";

        Model _model;
        Worker _worker;
        Model _recovery;
        Worker _recoveryWorker;
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

        // ---------------------------------------------------------------- diagnostics
        //
        // Five numbers, sampled on the policy step itself, that between them say whether a checkpoint is
        // being run inside the envelope it was trained in. They live here rather than in the overlay
        // because three of them are only visible from inside Step(): once the action has been clamped and
        // the observation clipped, the evidence that either happened is gone.
        //
        // Each is an exponential moving average over roughly the last second of policy steps (50 Hz,
        // alpha 0.02) - short enough to react to a fall, long enough not to flicker.
        const float Ema = 0.02f;

        static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Milliseconds of inference per policy step, averaged. What the frame budget pays.</summary>
        public float InferenceMs { get; private set; }

        /// <summary>
        /// Fraction of joints whose commanded target had to be clamped into the joint limits before it
        /// reached the drive. This is the clamp that actually bites - <c>actionClip</c> ships at 100 and
        /// so can only ever read zero - and it is the one that means the pose the policy asked for is not
        /// the pose the body was given. Sustained above ~0.1 is either <c>actionScale</c> set too high
        /// for this checkpoint, or rig limits that do not match the MJCF it was trained against.
        /// </summary>
        public float TargetClamping { get; private set; }

        /// <summary>
        /// Fraction of observation slots hitting <c>observationClip</c>. Above a few per cent this is a
        /// scale mismatch - one of the <c>*Scale</c> fields does not match what the checkpoint was
        /// normalised with - and every clipped slot is an input the policy has never seen.
        /// </summary>
        public float ObservationClipping { get; private set; }

        /// <summary>
        /// Mean absolute change in action between consecutive policy steps: jitter. A smooth gait sits
        /// low, a policy chattering against the PD gains runs several times that, and the fix is a larger
        /// action-rate penalty in training rather than anything on this side.
        /// </summary>
        public float ActionRate { get; private set; }

        /// <summary>Mean absolute action magnitude, to read <see cref="TargetClamping"/> against.</summary>
        public float ActionMagnitude { get; private set; }

        /// <summary>Policy steps actually taken per second: the control rate the body really got.</summary>
        public float MeasuredControlHz { get; private set; }
        float _hzWindowStart;
        int _hzWindowSteps;

        public bool IsReady => _ready;
        public bool HasModel => _worker != null;

        /// <summary>Whether a get-up policy was loaded and can be switched to.</summary>
        public bool HasRecoveryModel => _recoveryWorker != null;

        /// <summary>
        /// Drives the get-up policy instead of the running one.
        ///
        /// Both share the observation contract exactly — the get-up task was trained as a subclass of the
        /// run-to-target environment for precisely this reason — so the switch is a change of which worker
        /// the same observation vector is handed to. The only difference on this side is the command: the
        /// get-up policy was trained with it zeroed, because a body on the deck has nowhere to be going.
        /// </summary>
        public bool UseRecovery { get; set; }

        /// <summary>Which policy is actually driving right now, for the HUD and the telemetry overlay.</summary>
        public string ActiveModelName =>
            UseRecovery && recoveryModel != null ? recoveryModel.name : ModelName;
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
            if (rig == null) rig = GetComponentInChildren<AthleteRig>();
            if (rig == null) { Debug.LogError("[PolicyRunner] No AthleteRig.", this); return; }

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

            // The get-up policy is loaded here rather than when somebody falls. Building a Worker takes long
            // enough to be a visible hitch, and the frame an athlete hits the deck is the frame the
            // broadcast director cuts to them — the worst possible moment to stall.
            if (recoveryModel != null && _input != null)
            {
                try
                {
                    _recovery = ModelLoader.Load(recoveryModel);
                    _recoveryWorker = new Worker(_recovery, backend);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PolicyRunner] Failed to load recovery policy '{recoveryModel.name}': "
                                   + $"{e.Message}. Falls will end a run, as before.", this);
                    _recoveryWorker?.Dispose();
                    _recoveryWorker = null;
                    _recovery = null;
                }
            }
            UseRecovery = false;

            for (int i = 0; i < actDim; i++) _targets[i] = rig.DefaultPosition(i);
            rig.ApplyTargets(_targets);
            _stepCounter = 0;
            PolicySteps = 0;
            InferenceMs = TargetClamping = ObservationClipping = ActionRate = ActionMagnitude = 0f;
            MeasuredControlHz = 0f;
            _hzWindowStart = Time.unscaledTime;
            _hzWindowSteps = 0;
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

            // A recovering athlete gets a zero command, which is exactly what the get-up policy was trained
            // against. Ticking the command source anyway would leave it steering toward a finish line the
            // body cannot currently walk to, and the first thing it did on standing up would be to lurch.
            Vector3 cmd = Vector3.zero;
            if (commandSource != null && !UseRecovery)
            {
                commandSource.Tick(rig);
                cmd = commandSource.Command;
            }

            _obsBuilder.Fill(_obs, rig, cmd, _lastAction, _jointPos, _jointVel);

            MeasureObservationClipping();

            int n = _action.Length;
            Worker worker = UseRecovery && _recoveryWorker != null ? _recoveryWorker : _worker;
            if (worker != null)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _input.Upload(_obs);
                worker.Schedule(_input);
                var output = worker.PeekOutput() as Tensor<float>;
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
                InferenceMs += ((float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * TickMs) - InferenceMs) * Ema;
            }
            else
            {
                for (int i = 0; i < n; i++) _action[i] = 0f;
            }

            // Measured before _lastAction is overwritten below: the rate is the difference between this
            // action and the previous one, and this is the one line where both still exist.
            MeasureAction(n);

            float scale = config.actionScale;
            int clamped = 0;
            for (int i = 0; i < n; i++)
            {
                float want = rig.DefaultPosition(i) + _action[i] * scale;
                if (want < rig.LowerLimit(i) || want > rig.UpperLimit(i)) clamped++;
                _targets[i] = want;
                _lastAction[i] = _action[i];
            }
            TargetClamping += (clamped / (float)n - TargetClamping) * Ema;
            rig.ApplyTargets(_targets);
            PolicySteps++;

            _hzWindowSteps++;
            float window = Time.unscaledTime - _hzWindowStart;
            if (window >= 0.5f)
            {
                MeasuredControlHz = _hzWindowSteps / window;
                _hzWindowStart = Time.unscaledTime;
                _hzWindowSteps = 0;
            }
        }

        /// <summary>
        /// How much of the observation vector arrived at the rail. <see cref="ObservationBuilder"/> has
        /// already clamped it, so this compares against the clip value rather than looking for the
        /// original: a slot within a thousandth of the limit was, to any useful precision, clipped.
        /// </summary>
        void MeasureObservationClipping()
        {
            float clip = config.observationClip;
            if (clip <= 0f || _obs.Length == 0) return;
            float edge = clip * 0.999f;
            int hit = 0;
            for (int i = 0; i < _obs.Length; i++)
                if (_obs[i] >= edge || _obs[i] <= -edge) hit++;
            ObservationClipping += (hit / (float)_obs.Length - ObservationClipping) * Ema;
        }

        /// <summary>Jitter and magnitude of the action just produced, before it becomes a joint target.</summary>
        void MeasureAction(int n)
        {
            if (n == 0) return;
            float sumAbs = 0f, sumDelta = 0f;
            for (int i = 0; i < n; i++)
            {
                float a = _action[i];
                sumAbs += a < 0f ? -a : a;
                float d = a - _lastAction[i];
                sumDelta += d < 0f ? -d : d;
            }
            ActionMagnitude += (sumAbs / n - ActionMagnitude) * Ema;
            ActionRate += (sumDelta / n - ActionRate) * Ema;
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
            _recoveryWorker?.Dispose();
            _input = null;
            _worker = null;
            _recoveryWorker = null;
            _model = null;
            _recovery = null;
        }

        void OnDestroy() => DisposeWorker();
    }
}
