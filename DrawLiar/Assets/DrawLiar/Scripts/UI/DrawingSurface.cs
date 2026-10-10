using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawingSurface : VisualElement
    {
        private const int Width=1200, Height=800;
        private const int OTHER_STROKE_OPACITY_DIVISOR=4;
        private readonly Texture2D texture;
        private readonly Color32[] pixels = new Color32[Width*Height];
        private readonly DrawNetworkManager network;
        private bool drawing, dirty;
        private int capturedPointer=-1;
        private Vector2 last;
        private float lastSend;
        private bool _pressurePointer;
        private float _lastBrushSize;
        private int _browserPenStroke;
        private float _browserSampleTime;
#if UNITY_WEBGL && !UNITY_EDITOR
        private const int BROWSER_PEN_SAMPLE_COUNT=128;
        private const int BROWSER_PEN_SAMPLE_SIZE=6;
        private readonly float[] _browserPenSamples=new float[BROWSER_PEN_SAMPLE_COUNT*BROWSER_PEN_SAMPLE_SIZE];
        private bool _browserPenReady;
#endif
        private Texture2D _previewTexture;
        private Color32[] _previewPixels;
        private IReadOnlyList<DrawStroke> _previewStrokes;
        private int? _previewAuthorId;
        private DrawingMode _previewMode;
        private bool _previewDirty;
        private float _nextPreviewRefresh;
        public static readonly Color32 PaperColor = new Color32(255,255,255,255);
        public Color32 BrushColor = new Color32(40,43,39,255);
        public float BrushSize = .009f;
        public bool Eraser;
        public bool PressureEnabled = DrawBrushSettings.PressureEnabled;
        public bool HasAuthorPreview => _previewAuthorId.HasValue;
        public static float AspectRatio => Width/(float)Height;

        public DrawingSurface(DrawNetworkManager manager)
        {
            network = manager;
            name="drawing-surface";
            AddToClassList("drawing-surface");
            texture=new Texture2D(Width,Height,TextureFormat.RGBA32,false) { filterMode=FilterMode.Bilinear };
            style.backgroundImage=new StyleBackground(texture);
            ClearCanvas();
            RegisterCallback<PointerDownEvent>(Down);
            RegisterCallback<PointerMoveEvent>(Move);
            RegisterCallback<PointerUpEvent>(Up);
            RegisterCallback<PointerCaptureOutEvent>(e=>{if(e.pointerId==capturedPointer){drawing=false;capturedPointer=-1;}});
#if UNITY_WEBGL && !UNITY_EDITOR
            RegisterCallback<AttachToPanelEvent>(_=>_browserPenReady=DrawBrowserInterop.DrawBrowserPenInitialize()==1);
#endif
            schedule.Execute(Upload).Every(16);
            RegisterCallback<DetachFromPanelEvent>(_=>
            {
                CancelDrawing();
                Object.Destroy(texture);
                if(_previewTexture!=null)Object.Destroy(_previewTexture);
                _previewTexture=null;_previewPixels=null;_previewStrokes=null;
            });
        }

        public void ClearCanvas()
        {
            ClearAuthorPreview();
            for(var i=0;i<pixels.Length;i++)pixels[i]=PaperColor;
            dirty=true;
        }

        public void ShowAuthorPreview(int authorId,DrawingMode mode,IReadOnlyList<DrawStroke> strokes)
        {
            bool changed=_previewAuthorId!=authorId||_previewMode!=mode;
            StopDrawing();
            _previewAuthorId=authorId;_previewMode=mode;_previewStrokes=strokes??System.Array.Empty<DrawStroke>();
            if(_previewTexture==null)
            {
                _previewPixels=new Color32[Width*Height];
                _previewTexture=new Texture2D(Width,Height,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear};
            }
            _previewDirty=true;
            style.backgroundImage=new StyleBackground(_previewTexture);
            if(changed){_nextPreviewRefresh=0;Upload();}
        }

        public void ClearAuthorPreview()
        {
            if(!_previewAuthorId.HasValue)return;
            _previewAuthorId=null;_previewStrokes=null;_previewDirty=false;
            style.backgroundImage=new StyleBackground(texture);
            MarkDirtyRepaint();
        }

        public void FitTo(Vector2 availableSize)
        {
            float horizontalInset=resolvedStyle.borderLeftWidth+resolvedStyle.borderRightWidth+resolvedStyle.paddingLeft+resolvedStyle.paddingRight;
            float verticalInset=resolvedStyle.borderTopWidth+resolvedStyle.borderBottomWidth+resolvedStyle.paddingTop+resolvedStyle.paddingBottom;
            float width=Mathf.Max(0,Mathf.Min(availableSize.x-horizontalInset,(availableSize.y-verticalInset)*AspectRatio));
            style.width=width+horizontalInset;
            style.height=width/AspectRatio+verticalInset;
        }

        private Vector2 Normalize(Vector2 p) => new Vector2(Mathf.Clamp01((p.x-contentRect.xMin)/contentRect.width),Mathf.Clamp01((p.y-contentRect.yMin)/contentRect.height));
        private void Down(PointerDownEvent e)
        {
            var point=this.WorldToLocal(e.position);
#if UNITY_WEBGL && !UNITY_EDITOR
            if(_browserPenReady)
            {
                var bounds=panel?.visualTree.worldBound??default;
                if(e.pointerType=="pen"||bounds.width>0&&bounds.height>0
                    &&DrawBrowserInterop.DrawBrowserPenSuppress((e.position.x-bounds.x)/bounds.width,(e.position.y-bounds.y)/bounds.height)==1)return;
            }
#endif
            if(drawing || HasAuthorPreview || e.button!=0 || !network.CanDraw || !contentRect.Contains(point))return;
            drawing=true;capturedPointer=e.pointerId;last=Normalize(point);_pressurePointer=e.pointerType=="pen";
            _lastBrushSize=DrawBrushSettings.ResolveSize(BrushSize,PressureEnabled,_pressurePointer,e.pressure,Eraser);
            this.CapturePointer(e.pointerId);Emit(last,e.pressure);e.StopPropagation();
        }
        private void Move(PointerMoveEvent e)
        {
            if(!drawing || e.pointerId!=capturedPointer)return;
            e.StopPropagation();
            if(Time.unscaledTime-lastSend<.025f)return;
            Emit(Normalize(this.WorldToLocal(e.position)),e.pressure);
        }
        private void Up(PointerUpEvent e)
        {
            if(e.pointerId!=capturedPointer)return;
            if(drawing)Emit(Normalize(this.WorldToLocal(e.position)),0,false);StopDrawing();e.StopPropagation();
        }
        private void StopDrawing()
        {
            drawing=false;_pressurePointer=false;_browserPenStroke=0;var pointer=capturedPointer;capturedPointer=-1;
            if(pointer>=0&&this.HasPointerCapture(pointer))this.ReleasePointer(pointer);
        }
        public void CancelDrawing()
        {
            StopDrawing();
#if UNITY_WEBGL && !UNITY_EDITOR
            if(_browserPenReady)DrawBrowserInterop.DrawBrowserPenDiscard();
#endif
        }
        private void ProcessBrowserPenSample(int kind,int strokeId,Vector2 panelPoint,float pressure,float sampleTime)
        {
            if(kind==4){StopDrawing();return;}
            if(strokeId<=0||panel==null||!enabledInHierarchy||!network.CanDraw||HasAuthorPreview){StopDrawing();return;}
            var point=this.WorldToLocal(panelPoint);
            if(kind==1)
            {
                var target=panel.Pick(panelPoint);
                if(drawing||!enabledInHierarchy||!contentRect.Contains(point)||target==null
                    ||!ReferenceEquals(target,this)&&!Contains(target))return;
                drawing=true;capturedPointer=-1;_browserPenStroke=strokeId;_pressurePointer=true;
                last=Normalize(point);_browserSampleTime=sampleTime;
                _lastBrushSize=DrawBrushSettings.ResolveSize(BrushSize,PressureEnabled,true,pressure,Eraser);
                Emit(last,pressure);
                return;
            }
            if(!drawing||_browserPenStroke!=strokeId)return;
            if(kind==3)
            {
                var target=panel.Pick(panelPoint);
                if(contentRect.Contains(point)&&target!=null&&(ReferenceEquals(target,this)||Contains(target)))
                    Emit(Normalize(point),0,false);
                StopDrawing();
                return;
            }
            if(kind!=2||sampleTime-_browserSampleTime<.025f)return;
            if(!contentRect.Contains(point))return;
            var hit=panel.Pick(panelPoint);
            if(hit==null||!ReferenceEquals(hit,this)&&!Contains(hit)){StopDrawing();return;}
            _browserSampleTime=sampleTime;
            Emit(Normalize(point),pressure);
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        private void PollBrowserPen()
        {
            if(!_browserPenReady)return;
            int count=DrawBrowserInterop.DrawBrowserPenRead(_browserPenSamples,BROWSER_PEN_SAMPLE_COUNT);
            if(count<0){StopDrawing();return;}
            var bounds=panel?.visualTree.worldBound??default;
            if(bounds.width<=0||bounds.height<=0)return;
            for(int index=0;index<count;index++)
            {
                int offset=index*BROWSER_PEN_SAMPLE_SIZE;
                var point=new Vector2(bounds.x+_browserPenSamples[offset+2]*bounds.width,bounds.y+_browserPenSamples[offset+3]*bounds.height);
                ProcessBrowserPenSample((int)_browserPenSamples[offset],(int)_browserPenSamples[offset+1],point,_browserPenSamples[offset+4],_browserPenSamples[offset+5]);
            }
        }
#endif
        private void Emit(Vector2 point,float pressure,bool samplePressure=true)
        {
            if(!network.CanDraw||HasAuthorPreview) { StopDrawing();return; }
            float size=samplePressure?DrawBrushSettings.ResolveSize(BrushSize,PressureEnabled,_pressurePointer,pressure,Eraser):_lastBrushSize;
            var stroke=new DrawStroke { X1=last.x,Y1=last.y,X2=point.x,Y2=point.y,StartSize=_lastBrushSize,Size=size,R=BrushColor.r,G=BrushColor.g,B=BrushColor.b,Eraser=Eraser,CanvasVersion=network.CanvasVersion };
            network.SendStroke(stroke);last=point;_lastBrushSize=size;lastSend=Time.unscaledTime;
        }
        public void Apply(DrawStroke stroke)
        {
            Rasterize(stroke,pixels,0);
            dirty=true;
            if(HasAuthorPreview&&_previewMode==DrawingMode.Relay)_previewDirty=true;
        }

        private void Rasterize(DrawStroke stroke,Color32[] target,float expansion)
        {
            var a=new Vector2(stroke.X1*Width,(1-stroke.Y1)*Height);
            var b=new Vector2(stroke.X2*Width,(1-stroke.Y2)*Height);
            float startSize=stroke.StartSize>0?stroke.StartSize:stroke.Size;
            float startRadius=Mathf.Clamp(startSize*Width*.5f,1,48)+expansion;
            float endRadius=Mathf.Clamp(stroke.Size*Width*.5f,1,48)+expansion;
            var count=Mathf.Max(1,Mathf.CeilToInt(Vector2.Distance(a,b)/Mathf.Max(1,Mathf.Min(startRadius,endRadius)*.4f)));
            var color=expansion>0?new Color32(120,103,158,255):stroke.Eraser?PaperColor:new Color32(stroke.R,stroke.G,stroke.B,255);
            for(var step=0;step<=count;step++)
            {
                float progress=step/(float)count;
                var point=Vector2.Lerp(a,b,progress);
                float radius=Mathf.Lerp(startRadius,endRadius,progress);
                var x0=Mathf.Max(0,Mathf.FloorToInt(point.x-radius-1));var x1=Mathf.Min(Width-1,Mathf.CeilToInt(point.x+radius+1));
                var y0=Mathf.Max(0,Mathf.FloorToInt(point.y-radius-1));var y1=Mathf.Min(Height-1,Mathf.CeilToInt(point.y+radius+1));
                for(var y=y0;y<=y1;y++)for(var x=x0;x<=x1;x++)
                {
                    var distance=Vector2.Distance(new Vector2(x,y),point);
                    var alpha=Mathf.Clamp01(radius+.5f-distance);
                    if(alpha>0)
                    {
                        int index=y*Width+x;
                        target[index]=Color32.Lerp(target[index],color,alpha);
                    }
                }
            }
        }

        private void RenderPreview()
        {
            if(_previewMode==DrawingMode.Relay)
            {
                const int PAPER_BLEND=255*(OTHER_STROKE_OPACITY_DIVISOR-1)+OTHER_STROKE_OPACITY_DIVISOR/2;
                for(int i=0;i<pixels.Length;i++)
                {
                    var pixel=pixels[i];
                    _previewPixels[i]=new Color32((byte)((pixel.r+PAPER_BLEND)/OTHER_STROKE_OPACITY_DIVISOR),(byte)((pixel.g+PAPER_BLEND)/OTHER_STROKE_OPACITY_DIVISOR),(byte)((pixel.b+PAPER_BLEND)/OTHER_STROKE_OPACITY_DIVISOR),255);
                }
                foreach(var stroke in _previewStrokes)if(stroke.AuthorPlayerId==_previewAuthorId)Rasterize(stroke,_previewPixels,3);
                foreach(var stroke in _previewStrokes)if(stroke.AuthorPlayerId==_previewAuthorId)Rasterize(stroke,_previewPixels,0);
            }
            else
            {
                for(int i=0;i<_previewPixels.Length;i++)_previewPixels[i]=PaperColor;
                foreach(var stroke in _previewStrokes)if(stroke.AuthorPlayerId==_previewAuthorId)Rasterize(stroke,_previewPixels,0);
            }
            _previewTexture.SetPixels32(_previewPixels);_previewTexture.Apply(false);
            if(panel!=null)RuntimePanelUtils.SetTextureDirty(panel,_previewTexture);
            _previewDirty=false;
            _nextPreviewRefresh=Time.unscaledTime+.1f;
        }

        private void Upload()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PollBrowserPen();
#endif
            if(drawing&&(!network.CanDraw||HasAuthorPreview))StopDrawing();
            bool changed=dirty;
            if(dirty)
            {
                texture.SetPixels32(pixels);texture.Apply(false);
                if(panel!=null)RuntimePanelUtils.SetTextureDirty(panel,texture);
                dirty=false;
            }
            if(_previewDirty&&HasAuthorPreview&&Time.unscaledTime>=_nextPreviewRefresh){RenderPreview();changed=true;}
            if(changed)MarkDirtyRepaint();
        }
    }
}
