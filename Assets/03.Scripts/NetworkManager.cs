// 파일명: NetworkManager.cs
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance { get; private set; }

    [Header("테스트 설정")]
    public bool useMockServer = true; 

    [Header("가상 서버 물리 설정 (기본 주행)")]
    public float baseSpeed = 30f;   
    public float drag = 5f;         

    [Header("가상 서버 물리 설정 (순간 돌진)")]
    public float dashImpulseForce = 45f;
    public float dashDuration = 0.2f;    
    public float dashCooldown = 1.5f;    

    [Header("게임 오버 (십자형 맵 경계) 설정")]
    [Tooltip("중앙 광장의 대략적인 반지름")]
    public float centerRadius = 19f;  
    [Tooltip("다리의 Z축(가로) 또는 X축(세로) 절반 폭")]
    public float bridgeWidth = 5f;    
    [Tooltip("다리 끝쪽 스폰 지점까지의 최대 길이")]
    public float bridgeLength = 40f;

    [Header("서버 연결 정보")]
    public string serverIP = "127.0.0.1";
    public int tcpPort = 7777;
    public int udpPort = 7778;

    [Header("플레이어 및 프리팹 관리")]
    public int myPlayerId = -1;
    public GameObject[] playerPrefabs = new GameObject[4]; 
    public BumperCarController[] playerControllers = new BumperCarController[4];

    [Header("스폰 위치 관리")]
    public Transform[] spawnPoints = new Transform[4]; 

    private UdpClient udpClient;
    private Thread receiveThread;
    private bool isRunning = false;

    private Vector3[] mockPositions = new Vector3[4];
    private Vector3[] mockVelocities = new Vector3[4];
    private float[] dashTimers = new float[4];     
    private float[] dashCooldowns = new float[4];  
    private Vector3[] dashDirections = new Vector3[4]; 

    private ConcurrentQueue<NetworkPackets.StatePacket> stateQueue = new ConcurrentQueue<NetworkPackets.StatePacket>();

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Start()
    {
        for (int i = 0; i < 4; i++)
        {
            mockPositions[i] = GetInitialSpawnPosition(i);
            mockVelocities[i] = Vector3.zero;
            dashTimers[i] = 0f;
            dashCooldowns[i] = 0f;
            dashDirections[i] = Vector3.forward;
        }

        if (useMockServer)
        {
            Debug.LogWarning("⚠️ 가상 서버 모드(Mock Mode)로 실행합니다.");
            if (myPlayerId == -1) myPlayerId = 0; 
            SpawnPlayers();
            if (playerControllers[myPlayerId] != null) playerControllers[myPlayerId].gameObject.SetActive(true);
        }
        else
        {
            ConnectToServer();
        }
    }

    private void ConnectToServer()
    {
        try
        {
            // 1. TCP 서버 접속
            TcpClient tcpClient = new TcpClient();
            tcpClient.Connect(serverIP, tcpPort);
            NetworkStream stream = tcpClient.GetStream();
            
            byte[] idBuffer = new byte[4];
            stream.Read(idBuffer, 0, idBuffer.Length);
            myPlayerId = BitConverter.ToInt32(idBuffer, 0);
            tcpClient.Close();
            
            Debug.Log($"[네트워크] 서버 접속 성공! 할당된 ID: {myPlayerId}");

            if (myPlayerId == -1) return;
            
            // 2. 플레이어 생성
            SpawnPlayers();

            // 3. UDP 통신 설정 (이 로직은 try 블록 안에 있어야 안전합니다)
            udpClient = new UdpClient();
            udpClient.Connect(serverIP, udpPort);
            isRunning = true;

            // 4. 수신 쓰레드 시작
            receiveThread = new Thread(ReceiveUdpData);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            // 5. 서버에 나 여기 있다고 알리는 첫 패킷 전송
            NetworkPackets.InputPacket hello = new NetworkPackets.InputPacket 
            { 
                playerId = myPlayerId, 
                playerName = "NewPlayer" // 이름은 나중에 원하는 대로 수정하세요
            };
            SendInputPacket(hello);
            Debug.Log("서버에 UDP 등록용 Hello 패킷 전송 완료!");
        }
        catch (Exception e) 
        { 
            Debug.LogError($"[네트워크 에러] 접속 실패: {e.Message}");
        }
    }
    
    private Vector3 GetInitialSpawnPosition(int id)
    {
        if (spawnPoints != null && id < spawnPoints.Length && spawnPoints[id] != null) return spawnPoints[id].position;
        float x = (id == 0) ? -10.0f : (id == 1) ? 10.0f : 0.0f;
        float z = (id == 2) ? -10.0f : (id == 3) ? 10.0f : 0.0f;
        return new Vector3(x, 0.5f, z); 
    }

    private Quaternion GetInitialSpawnRotation(int id)
    {
        if (spawnPoints != null && id < spawnPoints.Length && spawnPoints[id] != null) return spawnPoints[id].rotation;
        return Quaternion.identity;
    }

    private void SpawnPlayers()
    {
        for (int i = 0; i < 4; i++)
        {
            if (playerPrefabs[i] != null)
            {
                Vector3 spawnPos = GetInitialSpawnPosition(i);
                Quaternion spawnRot = GetInitialSpawnRotation(i);
                GameObject obj = Instantiate(playerPrefabs[i], spawnPos, spawnRot);
                playerControllers[i] = obj.GetComponent<BumperCarController>();
                playerControllers[i].playerId = i;
                playerControllers[i].isLocalPlayer = (i == myPlayerId);
                obj.SetActive(false); 
            }
        }
    }

    private void Update()
    {
        // ★ 핵심: 가상 서버일 때 부스터 타이머와 쿨타임을 깎아주는 로직 (이게 빠져있었습니다!)
        if (useMockServer)
        {
            for (int i = 0; i < 4; i++)
            {
                if (dashCooldowns[i] > 0f) dashCooldowns[i] -= Time.deltaTime;
                if (dashTimers[i] > 0f) dashTimers[i] -= Time.deltaTime;
            }
        }

        while (stateQueue.TryDequeue(out NetworkPackets.StatePacket packet))
        {
            // 1. 들어온 패킷 정보 출력
            // Debug.Log($"[Update] 패킷 수신됨! ID: {packet.playerId}, Alive: {packet.isAlive}, AliveState: {packet.isAlive}");

            // 2. 인덱스 체크
            if (packet.playerId < 0 || packet.playerId >= 4)
            {
                Debug.LogError($"[Update] 잘못된 ID 수신: {packet.playerId}");
                continue;
            }

            // 3. 컨트롤러 존재 여부 체크
            if (playerControllers[packet.playerId] == null)
            {
                Debug.LogError($"[Update] {packet.playerId}번 플레이어 컨트롤러가 NULL입니다! (SpawnPlayers 확인 필요)");
                continue;
            }

            // 4. 활성화 로직
            if (!playerControllers[packet.playerId].gameObject.activeSelf)
            {
                playerControllers[packet.playerId].gameObject.SetActive(true);
                Debug.Log($"[Update] {packet.playerId}번 자동차 활성화 완료!");
            }
            
            playerControllers[packet.playerId].OnReceiveState(packet);
        }
    }

    public void SendInputPacket(NetworkPackets.InputPacket packet)
    {
        if (useMockServer)
        {
            SimulateServerLogic(packet);
            return;
        }

        if (!isRunning) return;
        int size = Marshal.SizeOf(packet);
        byte[] buffer = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(packet, ptr, true);
        Marshal.Copy(ptr, buffer, 0, size);
        Marshal.FreeHGlobal(ptr);
        udpClient.Send(buffer, buffer.Length);
    }

    private void SimulateServerLogic(NetworkPackets.InputPacket input)
    {
        int id = input.playerId;
        if (id < 0 || id >= 4) return;

        float dt = Time.deltaTime;

        if (input.isBoosting && dashCooldowns[id] <= 0f && dashTimers[id] <= 0f)
        {
            dashTimers[id] = dashDuration;   
            dashCooldowns[id] = dashCooldown; 

            Vector3 inputDir = new Vector3(input.inputX, 0f, input.inputY).normalized;
            
            if (inputDir.magnitude < 0.1f)
            {
                if (playerControllers[id] != null)
                    dashDirections[id] = playerControllers[id].transform.forward;
                else
                    dashDirections[id] = Vector3.forward;
            }
            else
            {
                dashDirections[id] = inputDir;
            }

            mockVelocities[id].x += dashDirections[id].x * dashImpulseForce;
            mockVelocities[id].z += dashDirections[id].z * dashImpulseForce;
        }

        if (dashTimers[id] > 0f)
        {
            mockVelocities[id].x += dashDirections[id].x * dashImpulseForce * 2f * dt;
            mockVelocities[id].z += dashDirections[id].z * dashImpulseForce * 2f * dt;
        }
        else if (input.isDriving)
        {
            mockVelocities[id].x += input.inputX * baseSpeed * dt;
            mockVelocities[id].z += input.inputY * baseSpeed * dt; 
        }

        mockVelocities[id].x -= mockVelocities[id].x * drag * dt;
        mockVelocities[id].z -= mockVelocities[id].z * drag * dt;

        mockPositions[id].x += mockVelocities[id].x * dt;
        mockPositions[id].z += mockVelocities[id].z * dt;

        float rotY = 0f;
        if (Mathf.Abs(mockVelocities[id].x) > 0.1f || Mathf.Abs(mockVelocities[id].z) > 0.1f)
        {
            rotY = Mathf.Atan2(mockVelocities[id].x, mockVelocities[id].z) * Mathf.Rad2Deg;
        }

        // 십자형 안전 구역 판정 알고리즘
        float absX = Mathf.Abs(mockPositions[id].x);
        float absZ = Mathf.Abs(mockPositions[id].z);
        bool currentAlive = false;

        if (new Vector2(absX, absZ).magnitude <= centerRadius) 
        {
            currentAlive = true;
        }
        else if (absZ <= bridgeWidth && absX <= bridgeLength) 
        {
            currentAlive = true;
        }
        else if (absX <= bridgeWidth && absZ <= bridgeLength) 
        {
            currentAlive = true;
        }

        NetworkPackets.StatePacket mockState = new NetworkPackets.StatePacket
        {
            playerId = id,
            playerName = input.playerName,
            posX = mockPositions[id].x,
            posY = mockPositions[id].y,
            posZ = mockPositions[id].z,
            rotY = rotY,
            velX = mockVelocities[id].x,
            velZ = mockVelocities[id].z,
            isBoosting = (dashTimers[id] > 0f), 
            isAlive = currentAlive 
        };

        stateQueue.Enqueue(mockState);
    }

    private void ReceiveUdpData()
    {
        IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);
        Debug.Log("[네트워크] UDP 수신 쓰레드 시작됨");
        
        while (isRunning)
        {
            try
            {
                // 패킷 수신
                byte[] data = udpClient.Receive(ref anyIP);
                
                // ★ [중요] 데이터가 실제로 들어오는지 확인하는 로그
                Debug.Log($"[네트워크] 패킷 수신! 크기: {data.Length} bytes");

                if (data.Length == Marshal.SizeOf(typeof(NetworkPackets.StatePacket)))
                {
                    IntPtr ptr = Marshal.AllocHGlobal(data.Length);
                    Marshal.Copy(data, 0, ptr, data.Length);
                    NetworkPackets.StatePacket packet = (NetworkPackets.StatePacket)Marshal.PtrToStructure(ptr, typeof(NetworkPackets.StatePacket));
                    Marshal.FreeHGlobal(ptr);
                    
                    stateQueue.Enqueue(packet);
                    Debug.Log($"[네트워크] 패킷 큐에 추가됨 (ID: {packet.playerId})");
                }
                else
                {
                    Debug.LogWarning($"[네트워크] 패킷 크기 불일치! 수신: {data.Length}, 기대: {Marshal.SizeOf(typeof(NetworkPackets.StatePacket))}");
                }
            }
            catch (Exception e) 
            { 
                Debug.LogError($"[UDP 에러] {e.Message}"); 
            }
        }
    }

    private void OnApplicationQuit()
    {
        isRunning = false;
        if (udpClient != null) udpClient.Close();
        if (receiveThread != null) receiveThread.Abort();
    }
}