﻿using System;
using System.IO;
using System.Numerics;

using theori;
using theori.Audio;
using theori.Graphics;
using theori.Gui;
using theori.IO;

using OpenRM;
using OpenRM.Audio.Effects;
using OpenRM.Convert;
using OpenRM.Voltex;
using NeuroSonic.GamePlay.Scoring;

namespace NeuroSonic.GamePlay
{
    [Flags]
    public enum AutoPlay
    {
        None = 0,

        Buttons = 0x01,
        Lasers = 0x02,

        ButtonsAndLasers = Buttons | Lasers,
    }

    public sealed class GameLayer : Layer
    {
        public override int TargetFrameRate => 288;

        public override bool BlocksParentLayer => true;

        private readonly AutoPlay m_autoPlay;

        private bool AutoButtons => (m_autoPlay & AutoPlay.Buttons) != 0;
        private bool AutoLasers => (m_autoPlay & AutoPlay.Lasers) != 0;

        private HighwayControl m_control;
        private HighwayView m_highwayView;

        private Panel m_foreUiRoot, m_backUiRoot;
        private CriticalLine m_critRoot;

        private Chart m_chart;
        private SlidingChartPlayback m_playback;
        private MasterJudge m_judge;

        private AudioEffectController m_audioController;
        private AudioTrack m_audio;
        private AudioSample m_slamSample;

        internal GameLayer(AutoPlay autoPlay = AutoPlay.None)
        {
            m_autoPlay = autoPlay;
        }

        public override void ClientSizeChanged(int width, int height)
        {
            m_highwayView.Camera.AspectRatio = Window.Aspect;
        }

        public override void Init()
        {
            m_slamSample = AudioSample.FromFile(@"skins\Default\audio\slam.wav");
            m_slamSample.Channel = Host.Mixer.MasterChannel;
            m_slamSample.Volume = 0.5f * 0.7f;

            m_highwayView = new HighwayView();
            m_control = new HighwayControl(HighwayControlConfig.CreateDefaultKsh168());

            m_playback = new SlidingChartPlayback(null);
            m_playback.ObjectHeadCrossPrimary += (dir, obj) =>
            {
                if (dir == PlayDirection.Forward)
                    m_highwayView.RenderableObjectAppear(obj);
                else m_highwayView.RenderableObjectDisappear(obj);
            };
            m_playback.ObjectTailCrossSecondary += (dir, obj) =>
            {
                if (dir == PlayDirection.Forward)
                    m_highwayView.RenderableObjectDisappear(obj);
                else m_highwayView.RenderableObjectAppear(obj);
            };

            // TODO(local): Effects wont work with backwards motion, but eventually the
            //  editor (with the only backwards motion support) will pre-render audio instead.
            m_playback.ObjectHeadCrossCritical += (dir, obj) =>
            {
                if (dir != PlayDirection.Forward) return;

                if (obj is Event evt)
                    PlaybackEventTrigger(evt, dir);
                else PlaybackObjectBegin(obj);
            };
            m_playback.ObjectTailCrossCritical += (dir, obj) =>
            {
                if (dir == PlayDirection.Backward && obj is Event evt)
                    PlaybackEventTrigger(evt, dir);
                else PlaybackObjectEnd(obj);
            };

            m_highwayView.ViewDuration = m_playback.LookAhead;

            m_foreUiRoot = new Panel()
            {
                Children = new GuiElement[]
                {
                    m_critRoot = new CriticalLine(),
                }
            };
        }

        internal void OpenChart()
        {
            if (RuntimeInfo.IsWindows)
            {
                var dialog = new OpenFileDialogDesc("Open Chart",
                                    new[] { new FileFilter("K-Shoot MANIA Files", "ksh") });

                var dialogResult = FileSystem.ShowOpenFileDialog(dialog);
                if (dialogResult.DialogResult == DialogResult.OK)
                {
                    string kshChart = dialogResult.FilePath;

                    string fileDir = Directory.GetParent(kshChart).FullName;
                    var ksh = KShootMania.Chart.CreateFromFile(kshChart);

                    string audioFileFx = Path.Combine(fileDir, ksh.Metadata.MusicFile ?? "");
                    string audioFileNoFx = Path.Combine(fileDir, ksh.Metadata.MusicFileNoFx ?? "");

                    string audioFile = audioFileNoFx;
                    if (File.Exists(audioFileFx))
                        audioFile = audioFileFx;

                    if (!File.Exists(audioFile))
                    {
                        Logger.Log("Couldn't find audio file for chart.");
                        return;
                    }

                    if (m_audio != null)
                    {
                        m_audioController.Stop();
                        m_audioController.Dispose();
                    }

                    var audio = AudioTrack.FromFile(audioFile);
                    audio.Channel = Host.Mixer.MasterChannel;
                    audio.Volume = ksh.Metadata.MusicVolume / 100.0f;

                    var chart = ksh.ToVoltex();

                    m_chart = chart;
                    m_audio = audio;

                    // TODO(local): properly dispose of old stuffs
                    m_playback.SetChart(chart);
                    m_judge = new MasterJudge(chart);
                    for (int i = 0; i < 6; i++)
                    {
                        int stream = i;
                        var judge = (ButtonJudge)m_judge[i];
                        judge.JudgementOffset = 0.032;
                        judge.OnTickProcessed += (when, result) =>
                        {
                            Logger.Log($"[{ stream }] { result.Kind } :: { (int)(result.Difference * 1000) } @ { when }");
                        };
                    }

                    m_control = new HighwayControl(HighwayControlConfig.CreateDefaultKsh168());
                    m_highwayView.Reset();

                    m_audio.Position = m_chart.Offset;
                    m_audioController = new AudioEffectController(8, m_audio, true)
                    {
                        RemoveFromChannelOnFinish = true,
                    };
                    m_audioController.Finish += () =>
                    {
                        Logger.Log("track complete");
                        Host.PopToParent(this);
                    };

                    time_t firstObjectTime = double.MaxValue;
                    for (int s = 0; s < m_chart.StreamCount; s++)
                        firstObjectTime = MathL.Min((double)firstObjectTime, m_chart.ObjectStreams[s].FirstObject?.AbsolutePosition.Seconds ?? double.MaxValue);

                    m_audioController.Position = MathL.Min(0.0, (double)firstObjectTime - 2);
                    m_audioController.Play();
                }
            }
        }

        public override void Destroy()
        {
            m_audioController?.Stop();

            m_audioController?.Dispose();
            //m_highwayView?.Dispose();
        }

        public override void Suspended()
        {
            m_audioController?.Stop();
        }

        public override void Resumed()
        {
            m_audioController?.Play();
        }

        private void PlaybackObjectBegin(OpenRM.Object obj)
        {
            if (obj is AnalogObject aobj)
            {
                if (obj.IsInstant)
                {
                    int dir = -MathL.Sign(aobj.FinalValue - aobj.InitialValue);
                    m_control.ShakeCamera(dir);

                    if (aobj.InitialValue == (aobj.Stream == 6 ? 0 : 1) && aobj.NextConnected == null)
                        m_control.ApplyRollImpulse(-dir);
                    m_slamSample.Play();
                }

                if (aobj.PreviousConnected == null)
                {
                    if (!AreLasersActive) m_audioController.SetEffect(6, currentLaserEffectDef, BASE_LASER_MIX);
                    currentActiveLasers[obj.Stream - 6] = true;
                }
            }
            else if (obj is ButtonObject bobj)
            {
                if (bobj.HasEffect)
                    m_audioController.SetEffect(obj.Stream, bobj.Effect);
                else m_audioController.RemoveEffect(obj.Stream);
            }
        }

        private void PlaybackObjectEnd(OpenRM.Object obj)
        {
            if (obj is AnalogObject aobj)
            {
                if (aobj.NextConnected == null)
                {
                    currentActiveLasers[obj.Stream - 6] = false;
                    if (!AreLasersActive) m_audioController.RemoveEffect(6);
                }
            }
            if (obj is ButtonObject bobj)
            {
                m_audioController.RemoveEffect(obj.Stream);
            }
        }

        private void PlaybackEventTrigger(Event evt, PlayDirection direction)
        {
            if (direction == PlayDirection.Forward)
            {
                switch (evt)
                {
                    case LaserApplicationEvent app: m_control.LaserApplication = app.Application; break;

                    // TODO(local): left/right lasers separate + allow both independent if needed
                    case LaserFilterGainEvent filterGain: laserGain = filterGain.Gain; break;
                    case LaserFilterKindEvent filterKind:
                    {
                        m_audioController.SetEffect(6, currentLaserEffectDef = filterKind.FilterEffect, m_audioController.GetEffectMix(6));
                    }
                    break;

                    case LaserParamsEvent pars:
                    {
                        if (pars.LaserIndex.HasFlag(LaserIndex.Left)) m_control.LeftLaserParams = pars.Params;
                        if (pars.LaserIndex.HasFlag(LaserIndex.Right)) m_control.RightLaserParams = pars.Params;
                    }
                    break;

                    case SlamVolumeEvent pars: m_slamSample.Volume = pars.Volume * 0.7f; break;
                }
            }

            switch (evt)
            {
                case SpinImpulseEvent spin: m_control.ApplySpin(spin.Params, spin.AbsolutePosition); break;
                case SwingImpulseEvent swing: m_control.ApplySwing(swing.Params, swing.AbsolutePosition); break;
                case WobbleImpulseEvent wobble: m_control.ApplyWobble(wobble.Params, wobble.AbsolutePosition); break;
            }
        }

        public override bool KeyPressed(KeyInfo key)
        {
            var cp = m_chart?.ControlPoints.MostRecent(m_audioController.Position);

            switch (key.KeyCode)
            {
                case KeyCode.ESCAPE:
                {
                    Host.PopToParent(this);
                } break;

                case KeyCode.RETURN:
                {
                    if (m_audioController == null) break;

                    time_t minStartTime = m_chart.TimeEnd;
                    for (int i = 0; i < 8; i++)
                    {
                        time_t startTime = m_chart[i].FirstObject?.AbsolutePosition ?? 0;
                        if (startTime < minStartTime)
                            minStartTime = startTime;
                    }

                    minStartTime -= 2;
                    if (minStartTime > m_audioController.Position)
                        m_audioController.Position = minStartTime;
                }
                break;

                case KeyCode.PAGEUP: m_audioController.Position += cp.MeasureDuration; break;

                default: return false;
            }

            return true;
        }

        public override bool ButtonPressed(ButtonInfo info)
        {
            if (info.DeviceIndex != InputManager.Gamepad.DeviceIndex) return false;

            switch (info.Button)
            {
                case 1: BtPress(0); break;
                case 2: BtPress(1); break;
                case 3: BtPress(2); break;
                case 7: BtPress(3); break;
                case 5: BtPress(4); break;
                case 6: BtPress(5); break;

                default: return false;
            }

            void BtPress(int streamIndex)
            {
                var result = (m_judge[streamIndex] as ButtonJudge).UserPressed(m_judge.Position);
                if (result == null)
                    m_highwayView.CreateKeyBeam(streamIndex, Vector3.One);
                else
                {
                    Vector3 color = Vector3.One;

                    bool isEarly = result?.Difference < 0.0;
                    switch (result?.Kind)
                    {
                        case JudgeKind.Perfect: color = new Vector3(1, 1, 0); break;
                        case JudgeKind.Critical: color = new Vector3(1, 1, 0); break;
                        case JudgeKind.Near: color = isEarly ? new Vector3(1.0f, 0, 0.5f) : new Vector3(0.5f, 1, 0.25f); break;
                        case JudgeKind.Miss: color = new Vector3(1, 0, 0); break;
                    }

                    m_highwayView.CreateKeyBeam(streamIndex, color);
                }
            }

            return true;
        }

        public override bool ButtonReleased(ButtonInfo info)
        {
            if (info.DeviceIndex != InputManager.Gamepad.DeviceIndex) return false;

            switch (info.Button)
            {
                case 1: BtRelease(0); break;
                case 2: BtRelease(1); break;
                case 3: BtRelease(2); break;
                case 7: BtRelease(3); break;
                case 5: BtRelease(4); break;
                case 6: BtRelease(5); break;

                default: return false;
            }

            void BtRelease(int streamIndex)
            {
                (m_judge[streamIndex] as ButtonJudge).UserReleased(m_judge.Position);
            }

            return true;
        }

        public override void Update(float delta, float total)
        {
            if (m_chart != null)
            {
                time_t position = m_audio?.Position ?? 0;

                m_judge.Position = position;
                m_control.Position = position;
                m_playback.Position = position;

                float GetPathValueLerped(int stream)
                {
                    var s = m_playback.Chart[stream];

                    var mrPoint = s.MostRecent<PathPointEvent>(position);
                    if (mrPoint == null)
                        return ((PathPointEvent)s.FirstObject)?.Value ?? 0;

                    if (mrPoint.HasNext)
                    {
                        float alpha = (float)((position - mrPoint.AbsolutePosition).Seconds / (mrPoint.Next.AbsolutePosition - mrPoint.AbsolutePosition).Seconds);
                        return MathL.Lerp(mrPoint.Value, ((PathPointEvent)mrPoint.Next).Value, alpha);
                    }
                    else return mrPoint.Value;
                }

                m_control.MeasureDuration = m_chart.ControlPoints.MostRecent(position).MeasureDuration;

                m_control.LeftLaserInput = GetTempRollValue(position, 6);
                m_control.RightLaserInput = GetTempRollValue(position, 7, true);

                m_control.Zoom = GetPathValueLerped(StreamIndex.Zoom);
                m_control.Pitch = GetPathValueLerped(StreamIndex.Pitch);
                m_control.Offset = GetPathValueLerped(StreamIndex.Offset);
                m_control.Roll = GetPathValueLerped(StreamIndex.Roll);

                m_highwayView.PlaybackPosition = position;

                UpdateEffects();
                m_audioController.EffectsActive = true;
            }

            m_control.Update(Time.Delta);
            m_control.ApplyToView(m_highwayView);

            m_highwayView.Update();

            {
                var camera = m_highwayView.Camera;

                var totalWorldTransform = m_highwayView.WorldTransform;
                var critLineTransform = m_highwayView.CritLineTransform;

                Vector2 critRootPosition = camera.Project(critLineTransform, Vector3.Zero);
                Vector2 critRootPositionWest = camera.Project(critLineTransform, new Vector3(-1, 0, 0));
                Vector2 critRootPositionEast = camera.Project(critLineTransform, new Vector3(1, 0, 0));
                Vector2 critRootPositionForward = camera.Project(critLineTransform, new Vector3(0, 0, -1));

                Vector2 critRotationVector = critRootPositionEast - critRootPositionWest;
                float critRootRotation = MathL.Atan(critRotationVector.Y, critRotationVector.X);

                m_critRoot.LaserRoll = m_highwayView.LaserRoll;
                m_critRoot.BaseRoll = m_control.Roll * 360;
                m_critRoot.EffectRoll = m_control.EffectRoll;
                m_critRoot.EffectOffset = m_control.EffectOffset;
                m_critRoot.Position = critRootPosition;
                m_critRoot.Rotation = MathL.ToDegrees(critRootRotation) + m_control.CritLineEffectRoll * 25;
            }
        }

        private void UpdateEffects()
        {
            UpdateLaserEffects();
        }

        private EffectDef currentLaserEffectDef = EffectDef.GetDefault(EffectType.PeakingFilter);
        private readonly bool[] currentActiveLasers = new bool[2];
        private readonly float[] currentActiveLaserAlphas = new float[2];

        private bool AreLasersActive => currentActiveLasers[0] || currentActiveLasers[1];

        private const float BASE_LASER_MIX = 0.7f;
        private float laserGain = 0.5f;

        private float GetTempRollValue(time_t position, int stream, bool oneMinus = false)
        {
            var s = m_playback.Chart[stream];

            var mrAnalog = s.MostRecent<AnalogObject>(position);
            if (mrAnalog == null || position > mrAnalog.AbsoluteEndPosition)
                return 0;

            float result = mrAnalog.SampleValue(position);
            if (oneMinus)
                return 1 - result;
            else return result;
        }

        private void UpdateLaserEffects()
        {
            if (!AreLasersActive)
            {
                m_audioController.SetEffectMix(6, 0);
                return;
            }

            float LaserAlpha(int index)
            {
                return GetTempRollValue(m_audio.Position, index + 6, index == 1);
            }

            if (currentActiveLasers[0])
                currentActiveLaserAlphas[0] = LaserAlpha(0);
            if (currentActiveLasers[1])
                currentActiveLaserAlphas[1] = LaserAlpha(1);

            float alpha;
            if (currentActiveLasers[0] && currentActiveLasers[1])
                alpha = Math.Max(currentActiveLaserAlphas[0], currentActiveLaserAlphas[1]);
            else if (currentActiveLasers[0])
                alpha = currentActiveLaserAlphas[0];
            else alpha = currentActiveLaserAlphas[1];

            m_audioController.UpdateEffect(6, alpha);

            float mix = BASE_LASER_MIX * laserGain;
            if (currentLaserEffectDef != null && currentLaserEffectDef.Type == EffectType.PeakingFilter)
            {
                if (alpha < 0.1f)
                    mix *= alpha / 0.1f;
                else if (alpha > 0.8f)
                    mix *= 1 - (alpha - 0.8f) / 0.2f;
            }

            m_audioController.SetEffectMix(6, mix);
        }

        public override void Render()
        {
            void DrawUiRoot(Panel root)
            {
                if (root == null) return;

                var viewportSize = new Vector2(Window.Width, Window.Height);
                using (var grq = new GuiRenderQueue(viewportSize))
                {
                    root.Position = Vector2.Zero;
                    root.RelativeSizeAxes = Axes.None;
                    root.Size = viewportSize;
                    root.Rotation = 0;
                    root.Scale = Vector2.One;
                    root.Origin = Vector2.Zero;

                    root.Render(grq);
                }
            }

            DrawUiRoot(m_backUiRoot);
            m_highwayView.Render();
            DrawUiRoot(m_foreUiRoot);
        }
    }
}