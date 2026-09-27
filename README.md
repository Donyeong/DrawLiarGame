# Liar’s Canvas

Unity 6000.3.20f1 기반 PC 그림 라이어 게임. Unity 프로젝트는 `DrawLiar/`입니다.

공개 저장소에는 Asset Store 패키지인 Dissonance, Feel, DOTween Pro 원본을 포함하지 않습니다. 음성 코드 컴파일에는 Dissonance 9.0.9와 Mirror 연동 패키지가 필요합니다. 로컬에서 수정한 Mirror 연동 파일도 제외되므로, 동일한 동작을 재현하려면 작업자가 보관한 연동 수정본을 복원해야 합니다.

## 실행

`Assets/DrawLiar/Scenes/DrawLiar.unity`를 열어 Play를 누릅니다. Windows 실행 파일은 Unity 메뉴 `DrawLiar > Build Windows Release`로 생성하며 출력은 `DrawLiar/Builds/WindowsRelease/DrawLiar.exe`입니다. 검사용 개발 빌드는 `DrawLiar > Build Windows`로 `DrawLiar/Builds/Windows/`에 생성합니다.

메인 메뉴에서 방 만들기·참가하기·옵션을 선택합니다. 방 만들기는 모드 → 규칙 → 방 정보 순서로 진행하며, 참가하기에서는 코드 입장과 공개방 목록을 따로 선택합니다. 왼쪽 캐릭터의 커스터마이징 버튼으로 색·장식·닉네임을 바꿉니다. 3~12명이 참가할 수 있고, 게임 도중 입장한 사람은 다음 게임까지 관전합니다. 방장이 나가면 방이 종료됩니다.

## 게임 규칙

역할 확인 → 그림 → 토론 → 반론 → 투표 → 라이어 공개 → 라이어의 정답 추측 → 결과 순으로 진행합니다. 방 만들기의 시간 설정에서 각 단계를 조절하며, 반론은 0초로 설정하면 건너뜁니다. 릴레이 모드는 이전 그림을 이어 그리고, 한 명씩 모드는 차례마다 캔버스를 비웁니다.

주제 선택 화면에서 여러 주제를 켜고 끌 수 있으며, 선택한 주제 중에서 매 라운드 무작위로 출제합니다. 주제는 하나 이상 선택해야 합니다.

승리 조건은 정해진 판수 후 최고점 또는 목표 점수 달성 중 선택합니다. 동점 최다 득표자는 모두 발각되며, 최종 점수가 같으면 공동 우승입니다. 모든 라이어는 발각 여부와 관계없이 정답을 제출할 수 있습니다.

마이크 기본 키는 **T**입니다. 설정에서 자동 음성 감지, 입력 장치, 음소거와 화면 움직임 줄이기를 선택합니다. 메뉴의 **플레이어 음량**에서 개별 볼륨·음소거를 조절합니다. 투표할 때는 플레이어 카드를 선택한 뒤 **투표하기**로 확정합니다. **Enter**로 채팅 입력창을 열고, **숨기기**로 제시어를 가릴 수 있습니다.

## 연결 설정

- Mirror와 UGS Lobby·Relay를 사용합니다. `Assets/DrawLiar/Resources/OnlineServicesConfig.asset`의 UGS 환경을 참가자 모두 동일하게 설정합니다. 익명 로그인에 별도 API 키는 필요 없습니다.
- Steam 친구 초대는 현재 Spacewar 테스트 App ID **480**으로 설정되어 있습니다. Steam에 로그인하고 오버레이를 켠 상태에서 실행합니다. 개발 빌드와 ID 480 테스트 빌드는 실행 파일 옆에 `steam_appid.txt`를 자동 생성하므로 실행 폴더를 작업 디렉터리로 사용합니다. Steam 정식 배포 때는 발급받은 게임 ID로 변경하며 Release 빌드는 이 파일을 제외합니다. [Steamworks 초기화 안내](https://partner.steamgames.com/doc/sdk/api#initialization_and_shutdown)
- 개발용 LAN 연결은 로컬 테스트용입니다. 서로 다른 인터넷 환경에서 접속할 때는 온라인 방을 사용합니다.
- 음성 자동 검증은 합성 입력으로 송수신 경로와 볼륨·음소거를 확인합니다. 물리 마이크 검증은 현재 검증 범위에서 제외합니다.

## 데이터와 디자인

주제·단어·득점은 `Assets/DrawLiar/Resources/DrawLiar/GameData.json`에서 조절합니다. 사용자가 만든 주제는 게임의 사용자 데이터 경로에 저장되며 방장이 선택합니다.

[Figma 디자인과 모션](https://www.figma.com/design/2fvAJI2HKsB6RDzOXfiPwX): 로비, 12인 플레이 화면, 캐릭터와 UI 컴포넌트. 런타임 UI는 UI Toolkit과 Pretendard를 사용합니다. Pretendard 라이선스는 폰트와 함께 포함되어 있습니다.

## 개발 검증

Unity 메뉴 `DrawLiar > Run Game Checks`는 게임 규칙, 커스텀 주제 파일, 전송 계층과 음성 패킷 검사를 실행합니다. Windows 개발 빌드 생성 후 `Tools/Run-NetworkSmoke.ps1 -Mode Relay` 또는 `-Mode Individual`로 다중 프로세스 게임·관전·연결 종료를 검사합니다. `-Online`을 추가하면 실제 UGS 사설방과 Relay를 사용합니다. `-PlayerCount 12`는 관전자 없이 12명 전원이 참가하는 검사를 실행합니다.

개발 빌드의 추가 검사는 다음과 같습니다. 같은 PC의 LAN 검사는 포트를 공유하므로 순서대로 실행합니다.

- `Tools/Run-UiSmoke.ps1`: 로비·방 설정, 그리기·지우개·채팅·제시어 표시, 토론·반론·투표와 연결 종료를 검사합니다. `-PlayerCount 3/6/12` 중 하나의 숫자로 참가 인원을 지정하고, 작은 창은 `-Width 1280 -Height 720`으로 검사합니다.
- `Tools/Run-PublicRoomSmoke.ps1`: 실제 UGS 공개·사설방 노출, 목록 입장과 로비 정리
- `Tools/Run-VoiceSmoke.ps1`: 합성 입력을 실제 Dissonance로 송수신하여 복호화, 개별 볼륨·음소거와 송신 제어 검사. `-Online`을 추가하면 실제 UGS Relay 경로도 검사합니다. 최종 스피커 출력은 음소거합니다.
