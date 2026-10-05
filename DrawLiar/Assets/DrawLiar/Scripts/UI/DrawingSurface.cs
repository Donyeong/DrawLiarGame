using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawingSurface : VisualElement
    {
        private const int Width=1200, Height=800;
        private readonly Texture2D texture;
        private readonly Color32[] pixels = new Color32[Width*Height];
        private readonly DrawNetworkManager network;
        private bool drawing, dirty;
        private int capturedPointer=-1;
        private Vector2 last;
        private float lastSend;
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
        public bool HasAuthorPreview => _previewAuthorId.HasValue;

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
            schedule.Execute(Upload).Every(16);
            RegisterCallback<DetachFromPanelEvent>(_=>
            {
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
            if(mode==DrawingMode.Relay&&(strokes==null||strokes.Count==0)){ClearAuthorPreview();return;}
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

        private Vector2 Normalize(Vector2 p) => new Vector2(Mathf.Clamp01(p.x/contentRect.width),Mathf.Clamp01(p.y/contentRect.height));
        private void Down(PointerDownEvent e)
        {
            if(drawing || HasAuthorPreview || e.button!=0 || !network.CanDraw)return;
            drawing=true;capturedPointer=e.pointerId;last=Normalize(e.localPosition);this.CapturePointer(e.pointerId);Emit(last);e.StopPropagation();
        }
        private void Move(PointerMoveEvent e)
        {
            if(!drawing || e.pointerId!=capturedPointer)return;
            e.StopPropagation();
            if(Time.unscaledTime-lastSend<.025f)return;
            Emit(Normalize(e.localPosition));
        }
        private void Up(PointerUpEvent e)
        {
            if(e.pointerId!=capturedPointer)return;
            if(drawing)Emit(Normalize(e.localPosition));StopDrawing();e.StopPropagation();
        }
        private void StopDrawing()
        {
            drawing=false;var pointer=capturedPointer;capturedPointer=-1;
            if(pointer>=0&&this.HasPointerCapture(pointer))this.ReleasePointer(pointer);
        }
        public void CancelDrawing() => StopDrawing();
        private void Emit(Vector2 point)
        {
            if(!network.CanDraw||HasAuthorPreview) { StopDrawing();return; }
            var stroke=new DrawStroke { X1=last.x,Y1=last.y,X2=point.x,Y2=point.y,Size=BrushSize,R=BrushColor.r,G=BrushColor.g,B=BrushColor.b,Eraser=Eraser,CanvasVersion=network.CanvasVersion };
            network.SendStroke(stroke);last=point;lastSend=Time.unscaledTime;
        }
        public void Apply(DrawStroke stroke)
        {
            Rasterize(stroke,pixels,0,false);
            dirty=true;
            if(HasAuthorPreview&&_previewMode==DrawingMode.Relay)_previewDirty=true;
        }

        private void Rasterize(DrawStroke stroke,Color32[] target,float expansion,bool restore)
        {
            var a=new Vector2(stroke.X1*Width,(1-stroke.Y1)*Height);
            var b=new Vector2(stroke.X2*Width,(1-stroke.Y2)*Height);
            var radius=Mathf.Clamp(stroke.Size*Width*.5f,1,48)+expansion;
            var count=Mathf.Max(1,Mathf.CeilToInt(Vector2.Distance(a,b)/Mathf.Max(1,radius*.4f)));
            var color=expansion>0?new Color32(120,103,158,255):stroke.Eraser?PaperColor:new Color32(stroke.R,stroke.G,stroke.B,255);
            for(var step=0;step<=count;step++)
            {
                var point=Vector2.Lerp(a,b,step/(float)count);
                var x0=Mathf.Max(0,Mathf.FloorToInt(point.x-radius-1));var x1=Mathf.Min(Width-1,Mathf.CeilToInt(point.x+radius+1));
                var y0=Mathf.Max(0,Mathf.FloorToInt(point.y-radius-1));var y1=Mathf.Min(Height-1,Mathf.CeilToInt(point.y+radius+1));
                for(var y=y0;y<=y1;y++)for(var x=x0;x<=x1;x++)
                {
                    var distance=Vector2.Distance(new Vector2(x,y),point);
                    var alpha=Mathf.Clamp01(radius+.5f-distance);
                    if(alpha>0)
                    {
                        int index=y*Width+x;
                        target[index]=Color32.Lerp(target[index],restore?pixels[index]:color,alpha);
                    }
                }
            }
        }

        private void RenderPreview()
        {
            if(_previewMode==DrawingMode.Relay)
            {
                System.Array.Copy(pixels,_previewPixels,pixels.Length);
                foreach(var stroke in _previewStrokes)if(stroke.AuthorPlayerId==_previewAuthorId)Rasterize(stroke,_previewPixels,3,false);
                foreach(var stroke in _previewStrokes)if(stroke.AuthorPlayerId==_previewAuthorId)Rasterize(stroke,_previewPixels,0,true);
            }
            else
            {
                for(int i=0;i<_previewPixels.Length;i++)_previewPixels[i]=PaperColor;
                foreach(var stroke in _previewStrokes)if(stroke.AuthorPlayerId==_previewAuthorId)Rasterize(stroke,_previewPixels,0,false);
            }
            _previewTexture.SetPixels32(_previewPixels);_previewTexture.Apply(false);
            if(panel!=null)RuntimePanelUtils.SetTextureDirty(panel,_previewTexture);
            _previewDirty=false;
            _nextPreviewRefresh=Time.unscaledTime+.1f;
        }

        private void Upload()
        {
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
