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
                if(IsMobile){swatch.style.backgroundColor=Color.clear;var dot=Box(swatch,"mobile-swatch-dot");dot.pickingMode=PickingMode.Ignore;dot.style.backgroundColor=(Color)color;AddSwatchGlow(dot);}
                else{swatch.style.backgroundColor=(Color)color;AddSwatchGlow(swatch);}
                SetTooltip(swatch,"색상 {0}",index+1);swatch.EnableInClassList("palette-row-end",index%4==3);_brushSwatches.Add(swatch);
                if(color.Equals(DrawingSurface.PaperColor))
                {swatch.name="white-brush";swatch.AddToClassList("white-brush");SetTooltip(swatch,"흰색");swatch.Q<VisualElement>(className:"mobile-swatch-dot")?.AddToClassList("white-brush-dot");}
            }
            CreateFavoriteBrushPalette(IsMobile?toolRow:drawingTools);
            Text(drawingTools,"굵기","secret-label");var sizes=Box(drawingTools,"brush-sizes");
            foreach(var width in new[]{5,11,24})
            {
                int size=width;
                var choice=Button(sizes,"●",()=>{surface.BrushSize=size/1200f;RefreshBrushControls();},"brush-size-option secondary");
                choice.userData=width/1200f;SetTooltip(choice,"굵기 {0}",width);choice.style.fontSize=width==5?8:width==11?12:18;
            }
            surface.BrushSize=11/1200f;
            if(!IsMobile){_pressureButton=CreatePressureToggle(drawingTools);_pressureButton.style.minWidth=94;_pressureButton.style.marginLeft=10;}
            RefreshPainterDrawingControls();
            RefreshBrushControls();
        }

        private void SelectBrushColor(Color32 color)
        {
            if(surface==null)return;
            surface.BrushColor=DrawBrushSettings.ResolveColor(color,lobby?.IsPainterSubscriber==true);surface.Eraser=false;RefreshBrushControls();
        }

        private void RefreshPainterDrawingControls()
        {
            if(surface==null)return;
            bool subscriber=lobby?.IsPainterSubscriber==true;
            var color=DrawBrushSettings.ResolveColor(surface.BrushColor,subscriber);
            if(!color.Equals(surface.BrushColor))
            {
                surface.CancelDrawing();surface.BrushColor=color;
                RefreshBrushControls();
            }
            void ShowTool(VisualElement tool)
            {
                if(tool==null)return;
                tool.style.display=subscriber?DisplayStyle.Flex:DisplayStyle.None;
            }
            ShowTool(_favoriteBrushPalette);
            RefreshFavoriteBrushControls();
            if(root!=null)root.Query<Button>("brush-pressure-toggle").ForEach(button=>
            {
                ShowTool(button);
                SetText(button,surface.PressureEnabled?"필압 끄기":"필압 켜기");
                button.EnableInClassList("tool-selected",surface.PressureEnabled);
            });
            var palettePopup=overlay?.Q<VisualElement>("brush-palette-popup");
            if(palettePopup!=null&&(!subscriber||palettePopup.userData as string!=lobby.Profile?.AccountId))CloseModal();
        }

        private void RefreshBrushControls()
        {
            if(surface==null)return;
            foreach(var swatch in _brushSwatches)
                swatch.EnableInClassList("selected",!surface.Eraser&&swatch.userData is Color32 color&&color.Equals(surface.BrushColor));
            _paletteButton?.EnableInClassList("tool-selected",!surface.Eraser&&!DrawBrushSettings.Palette.Take(DrawBrushSettings.BASE_COLOR_COUNT).Contains(surface.BrushColor));
            RefreshFavoriteBrushControls();
            if(drawingTools!=null)foreach(var choice in drawingTools.Query<Button>(className:"brush-size-option").ToList())
                choice.EnableInClassList("selected",choice.userData is float size&&Mathf.Approximately(size,surface.BrushSize));
            if(_pressureButton!=null)
            {
                SetText(_pressureButton,surface.PressureEnabled?"필압 끄기":"필압 켜기");
                _pressureButton.EnableInClassList("tool-selected",surface.PressureEnabled);
            }
        }

        private Button CreatePressureToggle(VisualElement parent)
        {
            Button button=null;
            button=Button(parent,surface.PressureEnabled?"필압 끄기":"필압 켜기",()=>
            {
                if(lobby?.IsPainterSubscriber!=true)return;
                surface.CancelDrawing();DrawBrushSettings.SetPressureEnabled(!surface.PressureEnabled);
                SetText(button,surface.PressureEnabled?"필압 끄기":"필압 켜기");
                button.EnableInClassList("tool-selected",surface.PressureEnabled);RefreshBrushControls();
            },"secondary");
            button.name="brush-pressure-toggle";button.EnableInClassList("tool-selected",surface.PressureEnabled);
            button.style.display=lobby?.IsPainterSubscriber==true?DisplayStyle.Flex:DisplayStyle.None;
            SetTooltip(button,"필압을 지원하는 펜의 누르는 세기에 따라 선 굵기가 달라집니다.");
            return button;
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
                surface.CancelDrawing();ClearDrawingPreview();
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
            if(!CanClearOwnDrawing())return;
            surface.CancelDrawing();_clearOwnPending=true;_clearOwnButton?.SetEnabled(false);
            network.ClearOwnStrokes();
        }

        private bool CanClearOwnDrawing()
        {
            var state=network?.State;
            return isActiveAndEnabled&&inRoom&&overlay==null&&_profileOverlay==null&&_roomPasswordOverlay==null
                &&_roomCustomizeOverlay==null&&_roomTopicWorkshopOverlay==null&&_workshopPreviewOverlay==null
                &&(!IsMobile||_mobileChatSheet==null||_mobileChatSheet.style.display==DisplayStyle.None)
                &&state!=null&&network.CanDraw&&surface?.panel!=null&&drawingTools?.panel!=null
                &&!surface.HasAuthorPreview&&!_clearOwnPending&&network.GetAuthorStrokes(state.LocalPlayerId).Count>0
                &&(state.Phase!=GamePhase.Drawing||state.RemainingSeconds>0&&_timerClock.Remaining(Time.realtimeSinceStartupAsDouble)>0);
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
            DrawCosmeticPresentation.ApplyNickname(_drawingPreviewName,player.Cosmetics);
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
            _clearOwnButton?.SetEnabled(CanClearOwnDrawing());
        }
    }
}
