# Liar’s Canvas

Unity 6000.3.20f1 기반 PC·Android 그림 라이어 게임입니다. Unity 프로젝트는 `DrawLiar/`, .NET 10 서버는 `Server/`에 있습니다.

메인서버 인증 후 게임서버에서 계정·커스터마이징·친구·상점·방 검색을 처리하고, 방에 참가하면 데디케이티드에 연결합니다. 데디케이티드는 게임 판정과 그림 기록을 관리합니다. 중도 참가자는 관전하며 방장 이탈 시 소유권을 이전하고 같은 계정의 재접속을 복원합니다.

클라이언트는 `Assets/DrawLiar/Scenes/DrawLiar.unity`에서 실행합니다. 서버 주소와 공개 인증서 SHA-256은 `Assets/DrawLiar/Resources/OnlineServicesConfig.asset`에서 설정합니다. Windows·Android에서 게스트 또는 Google 계정으로 접속하며, Google 로그인은 서버의 Desktop OAuth 설정과 Android Web Client ID·패키지/서명 등록이 필요합니다.

서버는 기존 Kona VM과 동일한 Ubuntu 24.04 x64에서 독립 `drawliar-dev` Docker Compose로 운영합니다. 메인 19050, 게임 19060, 데디케이티드 19070, 어드민 19080을 사용하며 PostgreSQL 15432는 localhost에만 노출합니다. 운영 설정과 인증서는 `/srv/drawliar/dev`, 배포 템플릿은 `/opt/drawliar/deploy`에 보관합니다. Google 환경 변수는 `GOOGLE_DESKTOP_CLIENT_ID`, `GOOGLE_DESKTOP_CLIENT_SECRET`, `GOOGLE_MOBILE_CLIENT_ID`입니다.

`Tools/LinuxServer/Build-Servers.ps1`은 게임 규칙을 검사하고 Linux 서버를 게시합니다. 기존 Jenkins의 `DrawLiar-Server` 작업이 같은 빌드 도구와 전용 배포 helper로 설치·백업·재배포·준비 검사를 수행합니다. 최초 설치는 해당 머신에 Docker Compose와 `kona-deploy` 계정을 준비한 뒤 `Tools/Docker/install.sh <공개주소>`를 sudo로 실행합니다. 배포 helper와 템플릿 갱신은 관리자 계정으로 수행하며 비밀 설정·DB 볼륨은 보존합니다.

`dotnet run --project Server/DrawLiar.IntegrationTests -- <Main HTTPS URL> <Admin HTTPS URL>`로 계정·아웃게임·다중 참가·동기화를 검증합니다. 자체 서명 인증서는 환경 변수 `DRAWLIAR_CERT_PIN`에 공개 DER SHA-256을 설정합니다. Unity의 `DrawLiar > Run Game Checks`는 게임 규칙·커스텀 주제·Google PKCE와 콜백 검증을 실행합니다.

주제·단어·득점은 `Assets/DrawLiar/Resources/DrawLiar/GameData.json`에서 설정합니다. 선택한 커스텀 주제는 방 생성 시 서버로 전송합니다. UI는 UI Toolkit과 Pretendard를 사용하며 폰트 라이선스는 해당 리소스에 포함되어 있습니다.

현지화에는 구매한 I2 Localization을 `Assets/I2/Localization`에 설치해야 합니다. 구매 에셋 원본은 저장소에 포함하지 않습니다.
