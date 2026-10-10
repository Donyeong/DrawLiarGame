using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private Button _clearOwnButton;
        private bool _clearOwnPending;
        private int? _previewAuthorId;
        private Label _drawingPreviewName;
        private VisualElement _drawingPreviewIdentity;
        private int _drawingPreviewRound=-1, _drawingPreviewArtist=-1, _drawingPreviewVersion=-1, _drawingPreviewEpoch=-1;
        private GamePhase _drawingPreviewPhase;
        private DrawingMode _drawingPreviewMode;
        private readonly List<Button> _brushSwatches = new List<Button>();
        private Button _paletteButton, _pressureButton;

        private void CreateDrawingTools(VisualElement context)
        {
            drawingTools=Box(context,"tools");
            _brushSwatches.Clear();_pressureButton=null;
            var toolRow=Box(drawingTools,"tool-row");
            Button(toolRow,"펜",()=>{surface.Eraser=false;RefreshBrushControls();},"secondary tool-selected");
            _clearOwnButton=Button(toolRow,"내 선 지우기",ClearOwnDrawing,"secondary clear-own-drawing");_clearOwnButton.name="clear-own-drawing";
            if(IsMobile)Button(toolRow,"굵기",BrushOptions,"secondary");
            var palette=Box(drawingTools,"palette");palette.name="drawing-palette";
            for(int index=0;index<DrawBrushSettings.BASE_COLOR_COUNT;index++)
            {
                Color32 color=DrawBrushSettings.Palette[index];
                var swatch=Button(palette,"",()=>SelectBrushColor(color),"swatch");
                swatch.userData=color;swatch.name="brush-color-"+index;
                if(IsMobile){swatch.style.backgroundColor=Color.clear;var dot=Box(swatch,"mobile-swatch-dot");dot.pickingMode=PickingMode.Ignore;dot.style.backgroundColor=(Color)color;}
                else swatch.style.backgroundColor=(Color)color;
                SetTooltip(swatch,"색상 {0}",index+1);swatch.EnableInClassList("palette-row-end",index%4==3);_brushSwatches.Add(swatch);
                if(color.Equals(DrawingSurface.PaperColor))
                {swatch.name="white-brush";swatch.AddToClassList("white-brush");SetTooltip(swatch,"흰색");swatch.Q<VisualElement>(className:"mobile-swatch-dot")?.AddToClassList("white-brush-dot");}
            }
            _paletteButton=Button(IsMobile?toolRow:drawingTools,"팔레트",BrushPalette,IsMobile?"secondary mobile-last":"secondary");
            _paletteButton.name="brush-palette-open";SetTooltip(_paletteButton,"더 많은 색상");
            _paletteButton.style.display=DisplayStyle.None;
            Text(drawingTools,"굵기","secret-label");var sizes=Box(drawingTools,"brush-sizes");
            foreach(var width in new[]{5,11,24})
            {
                int size=width;
                var choice=Button(sizes,"●",()=>{surface.BrushSize=size/1200f;RefreshBrushControls();},"brush-size-option secondary");
                choice.userData=width/1200f;SetTooltip(choice,"굵기 {0}",width);choice.style.fontSize=width==5?8:width==11?12:18;
            }
            surface.BrushSize=11/1200f;
            if(!IsMobile){_pressureButton=CreatePressureToggle(drawingTools);_pressureButton.style.minWidth=94;_pressureButton.style.marginLeft=10;}
            RefreshBrushControls();
        }

        private void SelectBrushColor(Color32 color)
        {
            if(surface==null)return;
            surface.BrushColor=color;surface.Eraser=false;RefreshBrushControls();
        }

        private void RefreshBrushControls()
        {
            if(surface==null)return;
            foreach(var swatch in _brushSwatches)
                swatch.EnableInClassList("selected",!surface.Eraser&&swatch.userData is Color32 color&&color.Equals(surface.BrushColor));
            _paletteButton?.EnableInClassList("tool-selected",!surface.Eraser&&!DrawBrushSettings.Palette.Take(DrawBrushSettings.BASE_COLOR_COUNT).Contains(surface.BrushColor));
            if(drawingTools!=null)foreach(var choice in drawingTools.Query<Button>(className:"brush-size-option").ToList())
                choice.EnableInClassList("selected",choice.userData is float size&&Mathf.Approximately(size,surface.BrushSize));
            if(_pressureButton!=null)SetText(_pressureButton,surface.PressureEnabled?"필압 끄기":"필압 켜기");
        }

        private Button CreatePressureToggle(VisualElement parent)
        {
            Button button=null;
            button=Button(parent,surface.PressureEnabled?"필압 끄기":"필압 켜기",()=>
            {
                surface.CancelDrawing();surface.PressureEnabled=!surface.PressureEnabled;
                DrawBrushSettings.SetPressureEnabled(surface.PressureEnabled);
                SetText(button,surface.PressureEnabled?"필압 끄기":"필압 켜기");
                button.EnableInClassList("tool-selected",surface.PressureEnabled);RefreshBrushControls();
            },"secondary");
            button.name="brush-pressure-toggle";button.EnableInClassList("tool-selected",surface.PressureEnabled);
            SetTooltip(button,"필압을 지원하는 펜의 누르는 세기에 따라 선 굵기가 달라집니다.");
            return button;
        }

        private void BrushPalette()
        {
            var modal=Modal("팔레트");modal.name="brush-palette-popup";
            var grid=Box(modal,"brush-expanded-palette");grid.name="brush-expanded-palette";
            grid.style.flexDirection=FlexDirection.Row;grid.style.flexWrap=Wrap.Wrap;grid.style.justifyContent=Justify.Center;
            for(int index=0;index<DrawBrushSettings.Palette.Count;index++)
            {
                Color32 color=DrawBrushSettings.Palette[index];
                var swatch=Button(grid,"",()=>{SelectBrushColor(color);CloseModal();},"swatch");
                swatch.name="brush-expanded-color-"+index;swatch.userData=color;
                swatch.style.width=44;swatch.style.minWidth=44;swatch.style.height=44;swatch.style.minHeight=44;swatch.style.flexShrink=0;
                swatch.style.marginLeft=4;swatch.style.marginRight=4;swatch.style.marginTop=4;swatch.style.marginBottom=4;
                swatch.style.borderTopLeftRadius=22;swatch.style.borderTopRightRadius=22;swatch.style.borderBottomLeftRadius=22;swatch.style.borderBottomRightRadius=22;
                swatch.style.backgroundColor=(Color)color;
                SetTooltip(swatch,color.Equals(DrawingSurface.PaperColor)?"흰색":"색상 {0}",index+1);
                swatch.EnableInClassList("selected",!surface.Eraser&&color.Equals(surface.BrushColor));
            }
            Button(modal,"닫기",CloseModal,"secondary");
        }

        private void BrushOptions()
        {
            var modal=Modal("붓 굵기");
            foreach(var width in new[]{5,11,24})
            {
                int size=width;var button=Button(modal,(size==5?"가는 선":size==11?"보통 선":"굵은 선"),()=>{surface.BrushSize=size/1200f;RefreshBrushControls();CloseModal();},"secondary");
                button.EnableInClassList("tool-selected",Mathf.Approximately(surface.BrushSize,size/1200f));
            }
            CreatePressureToggle(modal);
            Button(modal,"닫기",CloseModal,"secondary");
        }

        private void CreateDrawingPreviewName(VisualElement frame)
        {
            _drawingPreviewName=LeveledName(frame,"",1,"drawing-preview-name","drawing-preview-name");
            _drawingPreviewIdentity=_drawingPreviewName.parent;
            _drawingPreviewName.name="drawing-preview-name";
            _drawingPreviewName.pickingMode=PickingMode.Ignore;
            _drawingPreviewIdentity.style.display=DisplayStyle.None;
            var drawing=surface;var caption=_drawingPreviewIdentity;
            void PositionName()
            {
                if(surface!=drawing||_drawingPreviewIdentity!=caption||drawing.panel==null||drawing.parent!=frame||caption.parent!=frame)return;
                var paper=drawing.contentRect;
                if(!(paper.width>0&&paper.height>0))return;
                var origin=frame.WorldToLocal(drawing.LocalToWorld(paper.position));
                caption.style.left=origin.x+8;caption.style.top=origin.y+8;
                caption.style.maxWidth=Mathf.Max(0,Mathf.Min(paper.width*.7f,paper.width-16));
            }
            drawing.RegisterCallback<GeometryChangedEvent>(_=>PositionName());
            frame.RegisterCallback<GeometryChangedEvent>(_=>PositionName());
            PositionName();
            frame.RegisterCallback<PointerDownEvent>(evt=>
            {
                if(!IsMobile||!_previewAuthorId.HasValue||evt.button!=0||surface?.parent!=frame
                    ||!surface.contentRect.Contains(surface.WorldToLocal(evt.position)))return;
                ClearDrawingPreview();
                if(network.State!=null)RefreshDrawingInteractions(network.State);
                evt.StopImmediatePropagation();
            },TrickleDown.TrickleDown);
        }

        private void CreatePlayerProfileButton(VisualElement card,PlayerView player)
        {
            Button button=null;
            button=IconButton(card,"프로필 보기",DrawUIIcon.Kind.Profile,()=>OpenPublicProfile(player.AccountId,button),"player-profile-button");
            BindProfileTarget(button,player.AccountId);
            button.name="player-profile-"+player.Id;
            button.SetEnabled(System.Guid.TryParse(player.AccountId,out _));
        }

        private static bool IsPlayerProfileTarget(EventBase evt)
        {
            var target=evt.target as VisualElement;
            return target!=null&&(target.ClassListContains("player-profile-button")||target.GetFirstAncestorOfType<Button>()?.ClassListContains("player-profile-button")==true);
        }

        private void ClearOwnDrawing()
        {
            var state=network.State;
            if(state==null||!network.CanDraw||surface==null||surface.HasAuthorPreview||_clearOwnPending||network.GetAuthorStrokes(state.LocalPlayerId).Count==0)return;
            surface.CancelDrawing();_clearOwnPending=true;_clearOwnButton?.SetEnabled(false);
            network.ClearOwnStrokes();
        }

        private void PreviewAuthorDrawing(int authorId)
        {
            var state=network.State;
            var player=state?.Players.FirstOrDefault(value=>value.Id==authorId&&!value.IsSpectator);
            if(!inRoom||surface==null||player==null||state.Round<=0||state.Phase==GamePhase.Lobby||state.Phase==GamePhase.MatchResults)return;
            _previewAuthorId=authorId;
            if(state.Settings.Mode==DrawingMode.Individual)network.RequestAuthorDrawing(authorId);
            RefreshAuthorDrawingPreview();
        }

        private void RefreshAuthorDrawingPreview()
        {
            if(!_previewAuthorId.HasValue)return;
            var state=network.State;int authorId=_previewAuthorId.Value;
            var player=state?.Players.FirstOrDefault(value=>value.Id==authorId&&!value.IsSpectator);
            if(!inRoom||surface==null||player==null){ClearDrawingPreview();return;}
            surface.ShowAuthorPreview(authorId,state.Settings.Mode,network.GetAuthorStrokes(authorId));
            SetRawText(_drawingPreviewName,player.Name);
            _drawingPreviewIdentity.Q<DrawLevelBadge>()?.SetLevel(player.Level);
            _drawingPreviewIdentity.style.display=surface.HasAuthorPreview?DisplayStyle.Flex:DisplayStyle.None;
            RefreshAccusedSpotlight(state);
            RefreshDrawingInteractions(state);
        }

        private void ClearDrawingPreview()
        {
            if(network!=null)network.CancelQueuedAuthorDrawing();
            _previewAuthorId=null;surface?.ClearAuthorPreview();
            foreach(var card in playerCards.Values)card.RemoveFromClassList("preview-player");
            if(_drawingPreviewIdentity!=null)_drawingPreviewIdentity.style.display=DisplayStyle.None;
            if(network!=null&&network.State!=null)RefreshAccusedSpotlight(network.State);
        }

        private void OnAuthorDrawingChanged(int authorId)
        {
            if(authorId<0)ClearDrawingPreview();
            if(authorId<0||authorId==network.State?.LocalPlayerId&&network.GetAuthorStrokes(authorId).Count==0)_clearOwnPending=false;
            if(_previewAuthorId.HasValue&&(_previewAuthorId==authorId||surface?.HasAuthorPreview!=true))RefreshAuthorDrawingPreview();
            if(inRoom&&network.State!=null)RefreshDrawingInteractions(network.State);
        }

        private void RefreshDrawingInteractions(RoomSnapshot state)
        {
            bool changed=_drawingPreviewRound!=state.Round||_drawingPreviewPhase!=state.Phase||_drawingPreviewMode!=state.Settings.Mode||
                _drawingPreviewArtist!=state.ArtistId||_drawingPreviewVersion!=state.CanvasVersion||_drawingPreviewEpoch!=state.DrawingEpoch;
            if(changed){ClearDrawingPreview();_clearOwnPending=false;}
            _drawingPreviewRound=state.Round;_drawingPreviewPhase=state.Phase;_drawingPreviewMode=state.Settings.Mode;
            _drawingPreviewArtist=state.ArtistId;_drawingPreviewVersion=state.CanvasVersion;_drawingPreviewEpoch=state.DrawingEpoch;
            if(_previewAuthorId.HasValue&&!state.Players.Any(player=>player.Id==_previewAuthorId.Value&&!player.IsSpectator))ClearDrawingPreview();
            if(_previewAuthorId.HasValue&&state.Settings.Mode==DrawingMode.Individual)network.RequestAuthorDrawing(_previewAuthorId.Value);
            foreach(var entry in playerCards)entry.Value.EnableInClassList("preview-player",entry.Key==_previewAuthorId);
            _clearOwnButton?.SetEnabled(network.CanDraw&&surface?.HasAuthorPreview!=true&&!_clearOwnPending&&network.GetAuthorStrokes(state.LocalPlayerId).Count>0);
        }
    }
}
