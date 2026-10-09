# 타자 센서의 공만 렌더하는 화면

2026-10-09 사용자 요청으로 타자 관측 카메라를 검은 배경에 실제 공만 보이도록 변경했다. 이 계약은 기존 배경이 포함된 카메라 설명보다 우선한다.

- 포수 시점 위치·회전·FOV, 256×256 흑백·10ms 판단은 유지한다. 별도로 진행된30장 스택 변경을 보존하며 현재 입력 이력은290ms다. 공만 렌더하는 설정은 스택 수와 독립적이다.
- 공의 크기·재질·이동은 실제 환경의 공과 같다. 공을 확대하거나 공 위치·속도를 벡터에 추가하지 않는다.
- `BatterSensorBall` 사용자 레이어에 공 렌더러만 배치하고 센서의 Culling Mask를 그 레이어로 제한한다. 지면·선·존 표시·타자·배트·수비·관중석은 관측 영상에 렌더하지 않는다. 공이 화면 밖에 있으면 검은 영상이다.
- 센서는 Solid Color/검은색, Occlusion Culling·그림자·후처리를 끈다. 제외된 물체가 공을 가리거나 그림자로 배경 정보를 제공하지 않는다.
- 일반 카메라의 마스크와 장면 오브젝트 활성 상태는 변경하지 않는다. 기존 충돌 행렬에서 공 레이어는 Default와 같은 충돌을 사용한다. 경기장 간 간격400m와 센서 far clip180m는 다른 경기장의 공을 배제한다.
- `BatterAgent.Initialize`도 동일 설정을 적용한다. Editor 센서 갱신은 기존 위치/FOV·씬 참조·GUID를 보존하며 사용자 레이어가 없으면 첫 빈 사용자 슬롯에 만든다.

기존 학습 씬에서 `Tools > Baseball Simulation > Training > Update Batter Camera Sensors In Current Scene`을 실행하고 저장하면 적용된다. 새로 만드는 학습 씬에도 같은 설정을 사용한다. 레이어 변경은 일반 화면의 공과 지면/펜스 충돌을 유지하는지 실제 검증한다.

현재30장 전환 빌드 `Training/builds/BatterCamera30/BatterCamera30.exe`, `Training/builds/Stage2_Camera30/Stage2_Camera30.exe`, `Training/builds/Stage3_Camera30/Stage3_Camera30.exe`에 이 렌더 설정도 포함한다. 기존6장 실행 파일과 결과는 보존한다. 새 Player로 실행할 때 공만 보는 화면이 적용된다. 공만 렌더하는 변경 자체는 입력 크기를 바꾸지 않지만 별도의6→30장 전환에는 체크포인트 이식이 필요하다. 실행·이식·자동 연결은 [30장 안내](batter-camera-30.md)를 따른다. 영상 분포가 바뀌므로 성능 유지나 개선은 별도 평가가 필요하다.

`BatterAgentVerification.VerifyBallOnlyCamera`는 실제 CameraSensor 렌더로 공 픽셀, 공을 숨긴 완전 검은 영상, 앞쪽 장애물과의 픽셀 일치, 일반 카메라 마스크와 충돌 행렬을 검사한다. 기존 타석·타격·초기화 및 세 단계 검증도 수행한다. 실제 결과와 산출물은 [검증 기록](verification.md)에 남긴다.
