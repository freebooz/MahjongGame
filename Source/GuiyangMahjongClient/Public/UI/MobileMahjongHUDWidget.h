#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Network/MahjongNetworkTypes.h"
#include "MobileMahjongHUDWidget.generated.h"

class UButton; class UHorizontalBox; class UOverlay; class UTextBlock; class UVerticalBox; class UViewport; class UWidget; class UWrapBox;
class AMahjong3DTableActor;
class UMobileActionButtonPanel;
class UMobileHandTileWidget;
class UMobileErrorToastWidget;
class UMobileSettlementWidget;
class UTexture2D;

/** 游戏主 HUD。公共数据来自 GameState，私有手牌和操作列表来自所属 PlayerController Client RPC。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileMahjongHUDWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 绑定/解绑 GameState 与所属 PlayerController 事件，并每帧更新倒计时。 */
    virtual void NativeConstruct() override;
    virtual void NativeDestruct() override;
    virtual void NativeTick(const FGeometry& MyGeometry, float InDeltaTime) override;
    /** 顶部房间、牌墙、阶段、当前玩家、倒计时和翻鸡信息。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RoomId;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RemainingTileCount;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_CurrentPhase;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_CurrentTurnPlayer;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Countdown;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_FlippedJiTile;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_JiEvents;
    /** 旧版绿色金边背景层；仅保留资产序列化兼容性，运行时强制隐藏。 */
    UPROPERTY(meta=(BindWidgetOptional)) TObjectPtr<UWidget> Background_ComponentSlot;
    /** 三维牌桌嵌入区域及四个相对方位的手牌容器。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UViewport> Table3DViewport;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UHorizontalBox> Panel_SelfHandTiles;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UHorizontalBox> Panel_TopHandTiles;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_LeftHandTiles;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_RightHandTiles;
    /** 四个相对方位的弃牌区域。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UWrapBox> Panel_SelfDiscards;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UWrapBox> Panel_TopDiscards;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UWrapBox> Panel_LeftDiscards;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UWrapBox> Panel_RightDiscards;
    /** 四个相对方位的吃、碰、杠明牌区域。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_SelfMelds;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_TopMelds;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_LeftMelds;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_RightMelds;
    /** 四个方位的玩家信息文本；Self 永远映射到南方。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Top;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Left;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Right;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Self;
    /** 等待玩家/准备阶段直接叠加在三维房间上的操作，不再跳转独立准备页。 */
    UPROPERTY(meta=(BindWidgetOptional)) TObjectPtr<UButton> Btn_Ready;
    UPROPERTY(meta=(BindWidgetOptional)) TObjectPtr<UTextBlock> Btn_Ready_Label;
    UPROPERTY(meta=(BindWidgetOptional)) TObjectPtr<UTextBlock> Txt_ReadyStatus;
    UPROPERTY(meta=(BindWidgetOptional)) TObjectPtr<UButton> Btn_ReturnLobby;
    /** 动作按钮、弹层及按需创建的错误/结算控件。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UMobileActionButtonPanel> ActionButtonPanel;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UOverlay> PopupLayer;
    UPROPERTY(Transient) TObjectPtr<UMobileErrorToastWidget> ErrorToastInstance;
    UPROPERTY(Transient) TObjectPtr<UMobileSettlementWidget> SettlementInstance;
    /** 当前选中手牌和本地三维牌桌表现 Actor。 */
    UPROPERTY(Transient) TObjectPtr<UMobileHandTileWidget> SelectedHandTile;
    UPROPERTY(Transient) TObjectPtr<AMahjong3DTableActor> Table3DActor;
    /** Temporary room portraits; authoritative player images can replace these brushes later. */
    UPROPERTY(Transient) TObjectPtr<UTexture2D> PlaceholderAvatarA;
    UPROPERTY(Transient) TObjectPtr<UTexture2D> PlaceholderAvatarB;
    /** 最近一次公共/私有快照；UI 重建只读取缓存，不修改权威状态。 */
    UPROPERTY() FMahjongPublicTableState CachedPublicState;
    UPROPERTY() FMahjongPrivatePlayerState CachedPrivateState;
    bool bHasPrivateState = false;
    bool bVisualReviewMode = false;
    /** 网络事件入口：刷新公共/私有状态、动作、结算与错误。 */
    UFUNCTION() void HandlePublicTableState(const FMahjongPublicTableState& State);
    UFUNCTION() void HandlePrivateHand(const FMahjongPrivatePlayerState& State);
    UFUNCTION() void HandleAvailableActions(const TArray<FMahjongAction>& Actions);
    UFUNCTION() void HandleSettlement(const FMahjongSettlementResult& Result);
    UFUNCTION() void HandleFinalSettlement(const FMahjongFinalSettlementResult& Result);
    UFUNCTION() void HandleError(const FString& Message);
    /** 本地交互入口：选择手牌、准备和返回大厅。 */
    UFUNCTION() void HandleTileSelected(UMobileHandTileWidget* TileWidget);
    UFUNCTION() void HandleReady();
    UFUNCTION() void HandleReturnLobby();
public:
    /** 由根 HUD 或蓝图显式刷新房间、公共牌桌和私有手牌。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI")
    void RefreshRoomState(const FMahjongRoomState& State, int32 LocalSeat);
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void RefreshTableState(const FMahjongPublicTableState& State);
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void RefreshPrivateHand(const FMahjongPrivatePlayerState& State);
    /** 注入只读的本地截图预览数据，不发送任何牌局请求。 */
    void ApplyVisualReviewState(const FMahjongPublicTableState& PublicState,
        const FMahjongPrivatePlayerState& PrivateState, const TArray<FMahjongAction>& Actions);
    /** 将绝对座位旋转为本地玩家固定在南方的相对方位。 */
    static int32 GetRelativeSeatIndex(int32 AbsoluteSeat, int32 LocalSeat);
    static FString GetPhaseDisplayText(EMahjongTablePhase Phase);

private:
    /** 解析本地座位，并按快照重建手牌、对手牌背、弃牌和副露。 */
    int32 ResolveLocalSeat() const;
    void RebuildPrivateHand();
    void RefreshOpponentHands(int32 LocalSeat);
    void RefreshDiscards(int32 LocalSeat);
    void RefreshMelds(int32 LocalSeat);
    void RefreshJiDisplay();
    void ApplyPlaceholderAvatars();
    /** 把同一份缓存快照同步给三维桌面表现。 */
    void Refresh3DTable();
};
