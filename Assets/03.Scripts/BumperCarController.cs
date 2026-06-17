using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.InputSystem;

public class BumperCarController : MonoBehaviour
{
    [Header("플레이어 정보 설정")]
    public int playerId;
    public string myPlayerName = "Player";
    public bool isLocalPlayer;
    
    [Header("동기화 보간 설정")]
    public float interpolationSpeed = 15f;

    [Header("시각 이펙트 및 애니메이션")]
    public ParticleSystem boostEffect;
    public Animator animator; 
    
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 currentVelocity;
    private bool isAlive = true;

    private void Start()
    {
        targetPosition = transform.position;
        targetRotation = transform.rotation;

        // 인스펙터에서 애니메이터를 깜빡하고 안 넣었을 경우 자동 할당
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }

        // 로컬 플레이어일 경우 시네머신 카메라 세팅
        if (isLocalPlayer)
        {
            CinemachineCamera vcam = FindFirstObjectByType<CinemachineCamera>();
            if (vcam != null)
            {
                vcam.Follow = this.transform;
                vcam.LookAt = this.transform;

                // 카메라를 자동차의 등 뒤로 강제 정렬
                Vector3 behindPos = transform.position - transform.forward * 5f + Vector3.up * 3f;
                vcam.transform.position = behindPos;
                
                vcam.transform.rotation = Quaternion.LookRotation(transform.forward);

                // 카메라가 엉뚱한 곳에서 날아오는 현상을 방지하고 즉시 컷(Cut) 처리
                vcam.PreviousStateIsValid = false; 
            }
        }
    }

    private void Update()
    {
        if (!isAlive) return; // 사망 상태면 이동 연산 중지

        if (isLocalPlayer)
        {
            SendInput();
        }

        // ★ 핵심 변경점: 서버가 패킷을 안 보내는 프레임(16ms) 사이에도 현재 속도로 목표 위치를 스스로 전진시킵니다.
        targetPosition += currentVelocity * Time.deltaTime;
        
        // 계속 갱신되는 목표 지점을 향해 부드럽게 보간(Lerp)하며 따라갑니다.
        transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * interpolationSpeed);
        
        // 속도가 있을 때만 부드러운 회전(Slerp) 처리
        if (currentVelocity.magnitude > 0.1f)
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * interpolationSpeed);
        }

        // 애니메이션 상태 업데이트
        if (animator != null)
        {
            bool isMoving = currentVelocity.magnitude > 0.1f;
            animator.SetBool("isDrive", isMoving);
        }
    }

    private void SendInput()
    {
        float xInput = 0f;
        float yInput = 0f;
        bool isBoosting = false;

        // 1. 키보드 순수 입력값 감지
        if (Keyboard.current != null)
        {
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) xInput += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) xInput -= 1f;

            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) yInput += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) yInput -= 1f;

            isBoosting = Keyboard.current.spaceKey.isPressed;
        }

        // 2. 카메라가 바라보는 방향을 기준으로 입력 벡터를 글로벌 벡터로 변환
        Vector3 moveDir = Vector3.zero;
        Camera mainCam = Camera.main; // 씬에 있는 Main Camera (시네머신이 조종하는 카메라)

        if (mainCam != null)
        {
            Vector3 camForward = mainCam.transform.forward;
            Vector3 camRight = mainCam.transform.right;
            
            // Y축(위아래) 기울기는 무시하고 평면 이동만 계산
            camForward.y = 0;
            camRight.y = 0;
            camForward.Normalize();
            camRight.Normalize();

            // W/S는 카메라 정면/후면, A/D는 카메라 좌우 방향으로 매핑
            moveDir = (camForward * yInput + camRight * xInput).normalized;
        }

        bool drivingState = (moveDir.magnitude > 0.1f);

        // 3. 변환된 좌표를 서버 전송용 패킷에 담기
        NetworkPackets.InputPacket input = new NetworkPackets.InputPacket
        {
            playerId = this.playerId,
            playerName = this.myPlayerName,
            inputX = moveDir.x,  
            inputY = moveDir.z,  
            isDriving = drivingState,
            isBoosting = isBoosting
        };

        NetworkManager.Instance.SendInputPacket(input);
    }

    public void OnReceiveState(NetworkPackets.StatePacket packet)
    {
        // 살아있다가 방금 죽음 판정을 받았다면 (추락 연출 시작)
        if (isAlive && !packet.isAlive)
        {
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false; // 강제로 물리 엔진을 켬
                rb.useGravity = true;   // 중력 적용
            }

            // 부모/자식에 있는 '모든' 콜라이더를 찾아 비활성화
            Collider[] colliders = GetComponentsInChildren<Collider>();
            foreach (Collider coll in colliders)
            {
                coll.enabled = false; 
            }
        }

        targetPosition = new Vector3(packet.posX, packet.posY, packet.posZ);
        targetRotation = Quaternion.Euler(0, packet.rotY, 0);
        currentVelocity = new Vector3(packet.velX, 0, packet.velZ);
        isAlive = packet.isAlive;
        
        // 부스터 파티클 효과 켜기/끄기 동기화
        if (packet.isBoosting && boostEffect != null && !boostEffect.isPlaying)
            boostEffect.Play();
        else if (!packet.isBoosting && boostEffect != null && boostEffect.isPlaying)
            boostEffect.Stop();
    }
}