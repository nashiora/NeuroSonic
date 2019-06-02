﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CSCore;
using CSCore.Codecs;
using OpenGL;
using theori.Audio;
using theori.Audio.NVorbis;
using theori.Configuration;
using theori.Graphics;
using theori.IO;
using theori.Platform;

namespace theori
{
    public static class Host
    {
        public static IPlatform Platform { get; private set; }

        public static Mixer Mixer { get; private set; }
        
        public static GameConfig GameConfig { get; private set; }

        internal static ProgramPipeline Pipeline { get; private set; }

        private static readonly List<Layer> layers = new List<Layer>();
        private static readonly List<Overlay> overlays = new List<Overlay>();

        private static int LayerCount => layers.Count;
        private static int OverlayCount => overlays.Count;

        /// <summary>
        /// Adds a new, uninitialized layer to the top of the layer stack.
        /// The layer must never have been in the layer stack before.
        /// 
        /// This is to make sure that the initialization and destruction process
        ///  is well defined, no initialized or destroyed layers are allowed back in.
        /// </summary>
        public static void PushLayer(Layer layer)
        {
            if (layer.lifetimeState != Layer.LayerLifetimeState.Uninitialized)
            {
                throw new Exception("Layer has already been in the layer stack. Cannot re-initialize.");
            }

            layers.Add(layer);

            if (layer.BlocksParentLayer)
            {
                for (int i = LayerCount - 2; i > 0; i--)
                {
                    var nextLayer = layers[i];
                    nextLayer.Suspend();

                    if (nextLayer.BlocksParentLayer)
                    {
                        // if it blocks the previous layers then this has happened already for higher layers.
                        break;
                    }
                }
            }

            layer.Init();
            layer.lifetimeState = Layer.LayerLifetimeState.Alive;
        }

        /// <summary>
        /// Removes the topmost layer from the layer stack and destroys it.
        /// </summary>
        public static void PopLayer()
        {
            var layer = layers[LayerCount - 1];
            layers.RemoveAt(LayerCount - 1);

            layer.Destroy();
            layer.lifetimeState = Layer.LayerLifetimeState.Destroyed;

            if (layer.BlocksParentLayer)
            {
                int startIndex = LayerCount - 1;
                for (; startIndex > 0; startIndex--)
                {
                    if (layers[startIndex].BlocksParentLayer)
                    {
                        // if it blocks the previous layers then this will happen later for higher layers.
                        break;
                    }
                }

                // resume layers bottom to top
                for (int i = startIndex; i < LayerCount; i++)
                    layers[i].Resume();
            }
        }

        public static void Init(IPlatform platformImpl)
        {
            Platform = platformImpl;

            GameConfig = new GameConfig();
            // TODO(local): load config

            Window.Create();
            Window.VSync = VSyncMode.Off;
            Logger.Log($"Window VSync: { Window.VSync }");

            Window.ClientSizeChanged += OnClientSizeChanged;
            
            Window.Update();
            InputManager.Initialize();

            Pipeline = new ProgramPipeline();
            Pipeline.Bind();
            
            CodecFactory.Instance.Register("ogg-vorbis", new CodecFactoryEntry(s => new NVorbisSource(s).ToWaveSource(), ".ogg"));
            Mixer = new Mixer(2);
            Mixer.MasterChannel.Volume = 0.7f;

            #if DEBUG
            string cd = System.Reflection.Assembly.GetEntryAssembly().Location;
            while (!Directory.Exists(Path.Combine(cd, "InstallDir")))
                cd = Directory.GetParent(cd).FullName;
            Environment.CurrentDirectory = Path.Combine(cd, "InstallDir");
            #endif
        }

        private static void OnClientSizeChanged(int w, int h)
        {
            layers.ForEach(l => l.ClientSizeChanged(w, h));
            overlays.ForEach(l => l.ClientSizeChanged(w, h));
        }

        public static void Start(Layer initialState)
        {
            PushLayer(initialState);

            var timer = Stopwatch.StartNew();

            long lastFrameStart = timer.ElapsedMilliseconds;
            while (!Window.ShouldExitApplication)
            {
                int targetFrameRate = 0;

                int layerStartIndex = LayerCount - 1;
                for (; layerStartIndex >= 0; layerStartIndex--)
                {
                    var layer = layers[layerStartIndex];
                    targetFrameRate = MathL.Max(targetFrameRate, layer.TargetFrameRate);

                    if (layer.BlocksParentLayer)
                        break;
                }

                if (targetFrameRate == 0)
                    targetFrameRate = 60; // TODO(local): configurable target frame rate plz

                long targetFrameTimeMillis = 1_000 / targetFrameRate;

                long currentTime = timer.ElapsedMilliseconds;
                long elapsedTime = currentTime - lastFrameStart;

                bool updated = false;
                while (elapsedTime > targetFrameTimeMillis)
                {
                    updated = true;
                    lastFrameStart = currentTime;

                    elapsedTime -= targetFrameTimeMillis;

                    Time.Delta = targetFrameTimeMillis / 1_000.0f;
                    Time.Total = lastFrameStart / 1_000.0f;
                    
                    Keyboard.Update();
                    Mouse.Update();
                    Window.Update();

                    if (Window.ShouldExitApplication)
                    {
                        Quit();
                        return;
                    }

                    // update top down
                    for (int i = LayerCount - 1; i >= layerStartIndex; i--)
                        layers[i].Update(Time.Delta, Time.Total);
                }

                if (updated && Window.Width > 0 && Window.Height > 0)
                {
                    GL.ClearColor(0, 0, 0, 1);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                    // render bottom up
                    for (int i = layerStartIndex; i < LayerCount; i++)
                        layers[i].Render();

                    Window.SwapBuffer();
                }
            }
        }

        public static void Quit(int code = 0)
        {
            //Gamepad.Destroy();
            Window.Destroy();

            Environment.Exit(code);
        }
    }
}