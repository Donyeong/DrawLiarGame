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
