using System.Linq;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private Button _clearOwnButton;
        private bool _clearOwnPending;
        private int? _previewAuthorId;
        private Label _drawingPreviewName;
        private int _drawingPreviewRound=-1, _drawingPreviewArtist=-1, _drawingPreviewVersion=-1, _drawingPreviewEpoch=-1;
        private GamePhase _drawingPreviewPhase;
        private DrawingMode _drawingPreviewMode;

        private void CreateDrawingPreviewName(VisualElement frame)
        {
            _drawingPreviewName=RawText(frame,"","drawing-preview-name");
            _drawingPreviewName.name="drawing-preview-name";
            _drawingPreviewName.pickingMode=PickingMode.Ignore;
            _drawingPreviewName.style.display=DisplayStyle.None;
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
            if(IsMobile||!inRoom||surface==null||player==null||state.Round<=0||state.Phase==GamePhase.Lobby||state.Phase==GamePhase.MatchResults)return;
            _previewAuthorId=authorId;
            if(state.Settings.Mode==DrawingMode.Individual)network.RequestAuthorDrawing(authorId);
            RefreshAuthorDrawingPreview();
        }

        private void RefreshAuthorDrawingPreview()
        {
            if(!_previewAuthorId.HasValue)return;
            var state=network.State;int authorId=_previewAuthorId.Value;
            var player=state?.Players.FirstOrDefault(value=>value.Id==authorId&&!value.IsSpectator);
            if(IsMobile||!inRoom||surface==null||player==null){ClearDrawingPreview();return;}
            surface.ShowAuthorPreview(authorId,state.Settings.Mode,network.GetAuthorStrokes(authorId));
            SetRawText(_drawingPreviewName,player.Name);
            _drawingPreviewName.style.display=surface.HasAuthorPreview?DisplayStyle.Flex:DisplayStyle.None;
            RefreshAccusedSpotlight(state);
            RefreshDrawingInteractions(state);
        }

        private void ClearDrawingPreview()
        {
            if(network!=null)network.CancelQueuedAuthorDrawing();
            _previewAuthorId=null;surface?.ClearAuthorPreview();
            if(_drawingPreviewName!=null)_drawingPreviewName.style.display=DisplayStyle.None;
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
            if(changed||IsMobile){ClearDrawingPreview();if(changed)_clearOwnPending=false;}
            _drawingPreviewRound=state.Round;_drawingPreviewPhase=state.Phase;_drawingPreviewMode=state.Settings.Mode;
            _drawingPreviewArtist=state.ArtistId;_drawingPreviewVersion=state.CanvasVersion;_drawingPreviewEpoch=state.DrawingEpoch;
            if(_previewAuthorId.HasValue&&!state.Players.Any(player=>player.Id==_previewAuthorId.Value&&!player.IsSpectator))ClearDrawingPreview();
            if(!IsMobile&&_previewAuthorId.HasValue&&state.Settings.Mode==DrawingMode.Individual)network.RequestAuthorDrawing(_previewAuthorId.Value);
            _clearOwnButton?.SetEnabled(network.CanDraw&&surface?.HasAuthorPreview!=true&&!_clearOwnPending&&network.GetAuthorStrokes(state.LocalPlayerId).Count>0);
        }
    }
}
