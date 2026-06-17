#include <iostream>
#include <thread>
#include <vector>
#include <mutex>
#include <cstring>
#include <cmath>
#include <chrono>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#pragma comment(lib, "ws2_32.lib")
typedef int socklen_t;
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#define SOCKET int
#define INVALID_SOCKET -1
#define SOCKET_ERROR -1
#define closesocket close
#endif

const int TCP_PORT = 7777;
const int UDP_PORT = 7778;
const int MAX_PLAYERS = 4;

#pragma pack(push, 1)
struct InputPacket {
    int playerId;
    char playerName[16];
    float inputX;
    float inputY;
    bool isDriving;
    bool isBoosting;
};

struct StatePacket {
    int playerId;
    char playerName[16];
    float posX, posY, posZ;
    float rotY;
    float velX, velZ;
    bool isBoosting;
    bool isAlive;
};
#pragma pack(pop)

struct Player {
    bool isActive = false;
    bool isAlive = true;
    sockaddr_in udpAddress{};
    char name[16] = "Player";
    
    // 위치 및 물리 속성
    float posX = 0.0f, posY = 5.2f, posZ = 0.0f;
    float rotY = 0.0f;
    float velX = 0.0f, velZ = 0.0f;
    
    // 입력 속성
    float inputDirX = 0.0f, inputDirY = 0.0f;
    bool isDriving = false;
    bool isBoosting = false;
    
    // 부스터 속성
    float dashTimer = 0.0f;
    float dashCooldown = 0.0f;
    float dashDirX = 0.0f, dashDirZ = 1.0f;
};

Player players[MAX_PLAYERS];
std::mutex playerMutex;
SOCKET udpSocket;

// ★ 유저님이 검증하신 최종 물리/게임 설정 상수 적용
const float BASE_SPEED = 50.0f;     // 인스펙터 일치
const float DRAG = 4.0f;            // 인스펙터 일치
const float DASH_IMPULSE = 30.0f;   // 인스펙터 일치
const float DASH_DURATION = 0.2f;   // 인스펙터 일치
const float DASH_COOLDOWN_TIME = 1.5f; // 인스펙터 일치
const float CENTER_RADIUS = 19.0f;  // 인스펙터 일치
const float BRIDGE_WIDTH = 5.0f;    // 인스펙터 일치
const float BRIDGE_LENGTH = 40.0f;  // 인스펙터 일치
const float CAR_RADIUS = 2.5f;      // 충돌 크기 반경 (자동차끼리 겹치지 않게 하는 고정값)

void UdpReceiveThread() {
    char buffer[256];
    sockaddr_in clientAddr;
    socklen_t addrLen = sizeof(clientAddr);

    std::cout << "[서버] UDP 수신 스레드 시작..." << std::endl;

    while (true) {
        int bytesRead = recvfrom(udpSocket, buffer, sizeof(buffer), 0, (struct sockaddr*)&clientAddr, &addrLen);
        if (bytesRead < 0) continue;

        if (bytesRead == sizeof(InputPacket)) {
            InputPacket* input = reinterpret_cast<InputPacket*>(buffer);
            
            std::lock_guard<std::mutex> lock(playerMutex);
            if (input->playerId >= 0 && input->playerId < MAX_PLAYERS && players[input->playerId].isActive) {
                players[input->playerId].udpAddress = clientAddr;
                std::strncpy(players[input->playerId].name, input->playerName, 15);
                players[input->playerId].inputDirX = input->inputX;
                players[input->playerId].inputDirY = input->inputY;
                players[input->playerId].isDriving = input->isDriving;
                players[input->playerId].isBoosting = input->isBoosting;
            }
        }
    }
}

void TcpAcceptThread(SOCKET tcpSocket) {
    std::cout << "[서버] 매치메이킹 대기 중... (TCP: " << TCP_PORT << ", UDP: " << UDP_PORT << ")" << std::endl;
    
    while (true) {
        sockaddr_in clientAddr;
        socklen_t addrLen = sizeof(clientAddr);
        SOCKET clientSocket = accept(tcpSocket, (struct sockaddr*)&clientAddr, &addrLen);
        
        if (clientSocket == INVALID_SOCKET) continue;

        int newPlayerId = -1;
        {
            std::lock_guard<std::mutex> lock(playerMutex);
            for (int i = 0; i < MAX_PLAYERS; i++) {
                if (!players[i].isActive) {
                    newPlayerId = i;
                    players[i].isActive = true;
                    players[i].isAlive = true;
                    
                    // 스폰 포인트 (다리 끝 4방향)
                    if (newPlayerId == 0) {
                        players[i].posX = 0.0f; players[i].posY = 5.2f; players[i].posZ = -33.5f; players[i].rotY = 0.0f;
                    }
                    else if (newPlayerId == 1) {
                        players[i].posX = 0.0f; players[i].posY = 5.2f; players[i].posZ = 33.5f; players[i].rotY = 180.0f;
                    }
                    else if (newPlayerId == 2) {
                        players[i].posX = 34.5f; players[i].posY = 5.2f; players[i].posZ = 0.0f; players[i].rotY = -90.0f;
                    }
                    else if (newPlayerId == 3) {
                        players[i].posX = -34.5f; players[i].posY = 5.2f; players[i].posZ = 0.0f; players[i].rotY = 90.0f;
                    }
                    
                    players[i].velX = 0.0f;
                    players[i].velZ = 0.0f;
                    break;
                }
            }
        }

        send(clientSocket, reinterpret_cast<char*>(&newPlayerId), sizeof(int), 0);
        closesocket(clientSocket);

        if (newPlayerId != -1) {
            std::cout << "[서버] 클라이언트 접속. ID 할당 및 스폰 위치 지정: " << newPlayerId << std::endl;
        } else {
            std::cout << "[서버] 방이 꽉 찼습니다. 접속 거부." << std::endl;
        }
    }
}

void GameLoop() {
    const float dt = 1.0f / 60.0f;
    auto targetDuration = std::chrono::milliseconds(16);

    while (true) {
        auto startTime = std::chrono::steady_clock::now();

        {
            std::lock_guard<std::mutex> lock(playerMutex);
            
            // 1. 플레이어 이동 및 부스터 계산
            for (int id = 0; id < MAX_PLAYERS; id++) {
                if (!players[id].isActive || !players[id].isAlive) continue;

                if (players[id].dashCooldown > 0.0f) players[id].dashCooldown -= dt;
                if (players[id].dashTimer > 0.0f) players[id].dashTimer -= dt;

                if (players[id].isBoosting && players[id].dashCooldown <= 0.0f && players[id].dashTimer <= 0.0f) {
                    players[id].dashTimer = DASH_DURATION;
                    players[id].dashCooldown = DASH_COOLDOWN_TIME;
                    
                    float mag = std::sqrt(players[id].inputDirX * players[id].inputDirX + players[id].inputDirY * players[id].inputDirY);
                    if (mag > 0.1f) {
                        players[id].dashDirX = players[id].inputDirX / mag;
                        players[id].dashDirZ = players[id].inputDirY / mag;
                    } else {
                        players[id].dashDirX = std::sin(players[id].rotY * M_PI / 180.0f);
                        players[id].dashDirZ = std::cos(players[id].rotY * M_PI / 180.0f);
                    }

                    players[id].velX += players[id].dashDirX * DASH_IMPULSE;
                    players[id].velZ += players[id].dashDirZ * DASH_IMPULSE;
                }

                if (players[id].dashTimer > 0.0f) {
                    players[id].velX += players[id].dashDirX * DASH_IMPULSE * 2.0f * dt;
                    players[id].velZ += players[id].dashDirZ * DASH_IMPULSE * 2.0f * dt;
                } else if (players[id].isDriving) {
                    players[id].velX += players[id].inputDirX * BASE_SPEED * dt;
                    players[id].velZ += players[id].inputDirY * BASE_SPEED * dt;
                }

                players[id].velX -= players[id].velX * DRAG * dt;
                players[id].velZ -= players[id].velZ * DRAG * dt;

                players[id].posX += players[id].velX * dt;
                players[id].posZ += players[id].velZ * dt;

                if (std::abs(players[id].velX) > 0.1f || std::abs(players[id].velZ) > 0.1f) {
                    players[id].rotY = std::atan2(players[id].velX, players[id].velZ) * 180.0f / M_PI;
                }
            }

            // 2. ★ 플레이어 간의 물리 충돌 처리 (서로 밀어내기 및 탄성 충돌)
            for (int i = 0; i < MAX_PLAYERS; i++) {
                if (!players[i].isActive || !players[i].isAlive) continue;
                
                for (int j = i + 1; j < MAX_PLAYERS; j++) {
                    if (!players[j].isActive || !players[j].isAlive) continue;

                    float dx = players[j].posX - players[i].posX;
                    float dz = players[j].posZ - players[i].posZ;
                    float distSq = dx * dx + dz * dz;
                    float minDist = CAR_RADIUS * 2.0f;

                    // 두 자동차가 부딪혔다면!
                    if (distSq < minDist * minDist && distSq > 0.0001f) {
                        float dist = std::sqrt(distSq);
                        float nx = dx / dist;
                        float nz = dz / dist;

                        // 겹친 만큼 위치 강제 조정 (파고드는 현상 방지)
                        float overlap = minDist - dist;
                        players[i].posX -= nx * overlap * 0.5f;
                        players[i].posZ -= nz * overlap * 0.5f;
                        players[j].posX += nx * overlap * 0.5f;
                        players[j].posZ += nz * overlap * 0.5f;

                        // 충돌 시 서로 반대 방향으로 속도 튕겨내기 (탄성)
                        float rvx = players[j].velX - players[i].velX;
                        float rvz = players[j].velZ - players[i].velZ;
                        float velAlongNormal = rvx * nx + rvz * nz;

                        if (velAlongNormal < 0) {
                            float e = 0.8f; // 바운스 탄성 계수 (클수록 강하게 튕김)
                            float jImpulse = -(1.0f + e) * velAlongNormal / 2.0f;

                            players[i].velX -= jImpulse * nx;
                            players[i].velZ -= jImpulse * nz;
                            players[j].velX += jImpulse * nx;
                            players[j].velZ += jImpulse * nz;
                        }
                    }
                }
            }

            // 3. 생존 판정 (낙사) 및 데이터 브로드캐스트
            for (int id = 0; id < MAX_PLAYERS; id++) {
                if (!players[id].isActive) continue;

                if (players[id].isAlive) {
                    float absX = std::abs(players[id].posX);
                    float absZ = std::abs(players[id].posZ);
                    bool currentAlive = false;

                    if (std::sqrt(absX * absX + absZ * absZ) <= CENTER_RADIUS) currentAlive = true;
                    else if (absZ <= BRIDGE_WIDTH && absX <= BRIDGE_LENGTH) currentAlive = true;
                    else if (absX <= BRIDGE_WIDTH && absZ <= BRIDGE_LENGTH) currentAlive = true;

                    players[id].isAlive = currentAlive;
                }

                if (players[id].udpAddress.sin_family != 0) {
                    StatePacket stateOut;
                    stateOut.playerId = id;
                    std::strncpy(stateOut.playerName, players[id].name, 16);
                    stateOut.posX = players[id].posX;
                    stateOut.posY = players[id].posY;
                    stateOut.posZ = players[id].posZ;
                    stateOut.rotY = players[id].rotY;
                    stateOut.velX = players[id].velX;
                    stateOut.velZ = players[id].velZ;
                    stateOut.isBoosting = (players[id].dashTimer > 0.0f);
                    stateOut.isAlive = players[id].isAlive;

                    for (int targetId = 0; targetId < MAX_PLAYERS; targetId++) {
                        if (players[targetId].isActive && players[targetId].udpAddress.sin_family != 0) {
                            sendto(udpSocket, reinterpret_cast<char*>(&stateOut), sizeof(StatePacket), 0,
                                   (struct sockaddr*)&players[targetId].udpAddress, sizeof(players[targetId].udpAddress));
                        }
                    }
                }
            }
        }

        auto endTime = std::chrono::steady_clock::now();
        auto elapsedTime = std::chrono::duration_cast<std::chrono::milliseconds>(endTime - startTime);
        if (elapsedTime < targetDuration) {
            std::this_thread::sleep_for(targetDuration - elapsedTime);
        }
    }
}

int main() {
#ifdef _WIN32
    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);
#endif

    SOCKET tcpSocket = socket(AF_INET, SOCK_STREAM, 0);
    udpSocket = socket(AF_INET, SOCK_DGRAM, 0);

    sockaddr_in serverAddr;
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_addr.s_addr = INADDR_ANY;
    serverAddr.sin_port = htons(TCP_PORT);

    bind(tcpSocket, (struct sockaddr*)&serverAddr, sizeof(serverAddr));
    listen(tcpSocket, SOMAXCONN);

    serverAddr.sin_port = htons(UDP_PORT);
    bind(udpSocket, (struct sockaddr*)&serverAddr, sizeof(serverAddr));

    std::cout << "=== 범퍼카 서버 시작 (최종 동기화 버전) ===" << std::endl;

    std::thread acceptThread(TcpAcceptThread, tcpSocket);
    std::thread receiveThread(UdpReceiveThread);
    std::thread gameThread(GameLoop);

    acceptThread.join();
    receiveThread.join();
    gameThread.join();

#ifdef _WIN32
    closesocket(tcpSocket);
    closesocket(udpSocket);
    WSACleanup();
#endif

    return 0;
}
