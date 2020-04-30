using System;
using System.Numerics;
using System.Text;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace YALCT
{
    public class RuntimeContext : IDisposable
    {
        Sdl2Window window;
        bool windowResized = false;

        private GraphicsBackend backend;
        private bool isInitialized;

        private GraphicsDevice graphicsDevice;
        private ResourceFactory factory;
        private Swapchain swapchain;

        private CommandList commandList;
        private Pipeline pipeline;

        private DeviceBuffer vertexBuffer;
        private DeviceBuffer indexBuffer;
        private DeviceBuffer runtimeDataBuffer;
        private ResourceLayout resourceLayout;
        private ResourceSet resourceSet;

        private YALCTRuntimeData runtimeData;

        private ShaderDescription vertexShaderDesc;
        private Shader[] shaders;

        private ImGuiRenderer imGuiRenderer;

        public RuntimeContext(string[] args, GraphicsBackend backend = GraphicsBackend.Vulkan)
        {
            this.backend = backend;
            Initialize();
        }

        public void Initialize()
        {
            isInitialized = false;
            // SDL init
            WindowCreateInfo windowCI = new WindowCreateInfo()
            {
                WindowInitialState = WindowState.Maximized,
                WindowTitle = "Yet Another Live Coding Tool",
                WindowWidth = 200,
                WindowHeight = 200
            };
            window = VeldridStartup.CreateWindow(ref windowCI);
            window.Resized += () =>
            {
                windowResized = true;
            };

            // Veldrid init
            GraphicsDeviceOptions options = new GraphicsDeviceOptions(
                debug: false,
                swapchainDepthFormat: PixelFormat.R32_Float,
                syncToVerticalBlank: false,
                resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true,
                preferStandardClipSpaceYDirection: true);
#if DEBUG
            options.Debug = true;
#endif
            graphicsDevice = VeldridStartup.CreateGraphicsDevice(window, options, backend);
            factory = graphicsDevice.ResourceFactory;
            swapchain = graphicsDevice.MainSwapchain;
            swapchain.Name = "YALTC Main Swapchain";
            CreateResources();
            isInitialized = true;
        }

        public void CreateResources()
        {
            CreateRenderQuad();
            commandList = factory.CreateCommandList();
            CreateDynamicResources();
            CreateImGui();
        }

        private void CreateRenderQuad()
        {
            VertexPosition[] quadVertices =
               {
                new VertexPosition(new Vector3 (-1, 1, 0)),
                new VertexPosition(new Vector3 (1, 1, 0)),
                new VertexPosition(new Vector3 (-1, -1, 0)),
                new VertexPosition(new Vector3 (1, -1, 0))
            };
            uint[] quadIndices = new uint[]
            {
                0,
                1,
                2,
                1,
                3,
                2
            };
            vertexBuffer = factory.CreateBuffer(new BufferDescription(4 * VertexPosition.SizeInBytes, BufferUsage.VertexBuffer));
            vertexBuffer.Name = "YALCT Vertex Buffer";
            indexBuffer = factory.CreateBuffer(new BufferDescription(6 * sizeof(uint), BufferUsage.IndexBuffer));
            indexBuffer.Name = "YALCT Index Buffer";
            graphicsDevice.UpdateBuffer(vertexBuffer, 0, quadVertices);
            graphicsDevice.UpdateBuffer(indexBuffer, 0, quadIndices);
        }

        private void CreateDynamicResources()
        {
            // shaders
            if (!isInitialized)
            {
                vertexShaderDesc = CreateShaderDescription(VertexCode, ShaderStages.Vertex);
            }
            else
            {
                DisposeShaders();
            }
            ShaderDescription fragmentShaderDesc = CreateShaderDescription(fragmentCode, ShaderStages.Fragment);
            shaders = factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);

            // pipeline
            ResourceLayoutElementDescription[] layoutDescriptions = new ResourceLayoutElementDescription[]
            {
                new ResourceLayoutElementDescription ("RuntimeData", ResourceKind.UniformBuffer, ShaderStages.Fragment),
            };
            resourceLayout?.Dispose();
            resourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(layoutDescriptions));
            resourceLayout.Name = "YALCT Resource Layout";
            pipeline?.Dispose();
            pipeline = factory.CreateGraphicsPipeline(
                   new GraphicsPipelineDescription(
                       BlendStateDescription.SingleOverrideBlend,
                       new DepthStencilStateDescription(
                           depthTestEnabled: false,
                           depthWriteEnabled: false,
                           comparisonKind: ComparisonKind.Always),
                       new RasterizerStateDescription(
                           cullMode: FaceCullMode.Back,
                           fillMode: PolygonFillMode.Solid,
                           frontFace: FrontFace.Clockwise,
                           depthClipEnabled: false,
                           scissorTestEnabled: false),
                       PrimitiveTopology.TriangleList,
                       new ShaderSetDescription(
                           vertexLayouts: new VertexLayoutDescription[] {
                            new VertexLayoutDescription(
                                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
                            )
                           },
                           shaders: shaders),
                       new ResourceLayout[] { resourceLayout },
                       swapchain.Framebuffer.OutputDescription)
               );
            pipeline.Name = "YALCT Fullscreen Pipeline";

            runtimeDataBuffer?.Dispose();
            runtimeDataBuffer = factory.CreateBuffer(new BufferDescription(
                YALCTRuntimeData.Size,
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            runtimeDataBuffer.Name = "YALCT Runtime Data";
            BindableResource[] bindableResources = { runtimeDataBuffer };
            ResourceSetDescription resourceSetDescription = new ResourceSetDescription(resourceLayout, bindableResources);
            resourceSet?.Dispose();
            resourceSet = factory.CreateResourceSet(resourceSetDescription);
            resourceSet.Name = "YALCT Resource Set";
        }

        private void CreateImGui()
        {
            imGuiRenderer = new ImGuiRenderer(graphicsDevice,
                                              swapchain.Framebuffer.OutputDescription,
                                              window.Width,
                                              window.Height,
                                              ColorSpaceHandling.Linear);
            ImGui.StyleColorsDark();
            ImGui.GetStyle().Alpha = 0.3f;
        }

        private ShaderDescription CreateShaderDescription(string code, ShaderStages stage)
        {
            byte[] data = Encoding.UTF8.GetBytes(code);
            return new ShaderDescription(stage, data, "main");
        }

        public void Run()
        {
            DateTime previousTime = DateTime.Now;
            while (window.Exists)
            {
                if (!isInitialized) continue;
                DateTime newTime = DateTime.Now;
                float deltaTime = (float)(newTime - previousTime).TotalSeconds;

                if (window.Exists)
                {
                    if (windowResized)
                    {
                        Resize();
                    }
                    Update(deltaTime);
                    Render(deltaTime);
                }

                previousTime = newTime;
            }
            graphicsDevice.WaitForIdle();
        }

        private void Update(float deltaTime)
        {
            InputSnapshot inputSnapshot = window.PumpEvents();
            runtimeData.Update(window, inputSnapshot, deltaTime);

            imGuiRenderer.Update(deltaTime, inputSnapshot);
            SubmitImGui(deltaTime);
        }

        private void SubmitImGui(float deltaTime)
        {
            if (ImGui.BeginMainMenuBar())
            {
                if (ImGui.MenuItem("Run"))
                {
                    CreateDynamicResources();
                }
                string fps = $"{(int)MathF.Round(1f / deltaTime)}";
                Vector2 fpsSize = ImGui.CalcTextSize(fps);
                ImGui.SameLine(ImGui.GetWindowWidth() - fpsSize.X - 20);
                ImGui.Text(fps);
                ImGui.EndMainMenuBar();
            }
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
            if (ImGui.Begin("Shader Editor"))
            {
                Vector2 editorWindowSize = ImGui.GetWindowSize();
                ImGui.InputTextMultiline("", ref fragmentCode, MaxEditorStringLength, new Vector2(editorWindowSize.X - 15, editorWindowSize.Y - 40));
                ImGui.End();
            }
            ImGui.PopStyleVar();
        }

        private void Render(float deltaTime)
        {
            RenderShader();
            RenderImGui();
            graphicsDevice.WaitForIdle();

            // doing a final check if window was closed in middle of rendering
            if (window.Exists)
            {
                graphicsDevice.SwapBuffers(swapchain);
            }
        }

        private void RenderShader()
        {
            commandList.Begin();
            commandList.UpdateBuffer(runtimeDataBuffer, 0, runtimeData);

            commandList.SetFramebuffer(swapchain.Framebuffer);
            commandList.ClearColorTarget(0, RgbaFloat.Black);

            commandList.SetPipeline(pipeline);
            commandList.SetGraphicsResourceSet(0, resourceSet);
            commandList.SetVertexBuffer(0, vertexBuffer);
            commandList.SetIndexBuffer(indexBuffer, IndexFormat.UInt32);

            commandList.DrawIndexed(
                indexCount: 6,
                instanceCount: 1,
                indexStart: 0,
                vertexOffset: 0,
                instanceStart: 0);

            commandList.End();

            graphicsDevice.SubmitCommands(commandList);
        }

        private void RenderImGui()
        {
            commandList.Begin();
            commandList.SetFramebuffer(swapchain.Framebuffer);
            imGuiRenderer.Render(graphicsDevice, commandList);
            commandList.End();

            graphicsDevice.SubmitCommands(commandList);
        }

        private void Resize()
        {
            windowResized = false;
            graphicsDevice.ResizeMainWindow((uint)window.Width, (uint)window.Height);
            imGuiRenderer.WindowResized(window.Width, window.Height);
        }

        private void DisposeShaders()
        {
            if (shaders == null) return;
            foreach (Shader shader in shaders)
            {
                shader.Dispose();
            }
        }

        public void Dispose()
        {
            imGuiRenderer.Dispose();
            pipeline.Dispose();
            DisposeShaders();
            resourceSet.Dispose();
            resourceLayout.Dispose();
            commandList.Dispose();
            runtimeDataBuffer.Dispose();
            indexBuffer.Dispose();
            vertexBuffer.Dispose();
            graphicsDevice.Dispose();
        }

        private const int MaxEditorStringLength = 1000000;
        private const string VertexCode = @"
#version 450

layout(location = 0) in vec3 Position;

void main()
{
    gl_Position = vec4(Position, 1);
}";

        private string fragmentCode = @"
#version 450

layout(set = 0, binding = 0) uniform RuntimeData
{
    vec4 mouse;
    vec2 resolution;
    float time;
    float deltaTime;
    int frame;
};

layout(location = 0) out vec4 out_Color;

void main()
{
    float x = gl_FragCoord.x / resolution.x; 
    float y = gl_FragCoord.y / resolution.y;
    out_Color = vec4(abs(cos(time)),x,y,1);
}";

    }
}