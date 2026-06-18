using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.InputSystem;

public class BumperCarController : MonoBehaviour
{
   
    public int playerId;
    public string myPlayerName = "Player";
    public bool isLocalPlayer;

    
    public float interpolationSpeed = 15f;

    
    public Transform[] wheels;
    public float wheelRotationSpeed = 360f;

    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 currentVelocity;
    private bool isAlive = true;

    private void Start()
    {
        targetPosition = transform.position;
        targetRotation = transform.rotation;

        if (isLocalPlayer)
        {
            SetupCinemachineCamera();
        }
    }

    private void Update()
    {
        if (!isAlive) return;

        if (isLocalPlayer)
        {
            SendInput();
        }

        // 데드 레코닝 및 위치 보간
        targetPosition += currentVelocity * Time.deltaTime;
        transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * interpolationSpeed);

        // 이동 중일 때만 회전 및 바퀴 애니메이션 처리
        if (currentVelocity.sqrMagnitude > 0.01f)
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * interpolationSpeed);
            RotateWheels();
        }
    }

    private void SetupCinemachineCamera()
    {
        CinemachineCamera vcam = FindFirstObjectByType<CinemachineCamera>();
        if (vcam != null)
        {
            vcam.Follow = this.transform;
            vcam.LookAt = this.transform;

            Vector3 behindPos = transform.position - transform.forward * 5f + Vector3.up * 3f;
            vcam.transform.position = behindPos;
            vcam.transform.rotation = Quaternion.LookRotation(transform.forward);

            vcam.PreviousStateIsValid = false; // 카메라 순간이동 방지
        }
    }

    private void RotateWheels()
    {
        if (wheels == null || wheels.Length == 0) return;

        float speed = currentVelocity.magnitude;
        float direction = Vector3.Dot(transform.forward, currentVelocity.normalized) >= 0 ? 1f : -1f;
        float rotationAmount = speed * direction * wheelRotationSpeed * Time.deltaTime;

        foreach (Transform wheel in wheels)
        {
            if (wheel != null)
            {
                wheel.Rotate(Vector3.right, rotationAmount, Space.Self);
            }
        }
    }

    private void SendInput()
    {
        float xInput = 0f;
        float yInput = 0f;
        bool isBoosting = false;

        // 키보드 입력 감지
        if (Keyboard.current != null)
        {
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) xInput += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) xInput -= 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) yInput += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) yInput -= 1f;

            isBoosting = Keyboard.current.spaceKey.isPressed;
        }

        // 카메라 방향 기준 로컬 벡터 변환
        Vector3 moveDir = Vector3.zero;
        Camera mainCam = Camera.main;

        if (mainCam != null)
        {
            Vector3 camForward = mainCam.transform.forward;
            Vector3 camRight = mainCam.transform.right;

            camForward.y = 0;
            camRight.y = 0;

            moveDir = (camForward.normalized * yInput + camRight.normalized * xInput).normalized;
        }

        // 서버 전송
        NetworkPackets.InputPacket input = new NetworkPackets.InputPacket
        {
            playerId = this.playerId,
            playerName = this.myPlayerName,
            inputX = moveDir.x,
            inputY = moveDir.z,
            isDriving = moveDir.sqrMagnitude > 0.01f,
            isBoosting = isBoosting
        };

        NetworkManager.Instance.SendInputPacket(input);
    }

    public void OnReceiveState(NetworkPackets.StatePacket packet)
    {
        // 낙사 처리
        if (isAlive && !packet.isAlive)
        {
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
            }

            Collider[] colliders = GetComponentsInChildren<Collider>();
            foreach (Collider coll in colliders)
            {
                coll.enabled = false;
            }
        }

        // 서버 데이터 동기화
        targetPosition = new Vector3(packet.posX, packet.posY, packet.posZ);
        targetRotation = Quaternion.Euler(0, packet.rotY, 0);
        currentVelocity = new Vector3(packet.velX, 0, packet.velZ);
        isAlive = packet.isAlive;
    }
}