// Copyright 1998-2016 Epic Games, Inc. All Rights Reserved.

#include "MovieSceneCaptureDialogModule.h"
#include "MovieSceneCapture.h"

#include "UnrealEdMisc.h"

#include "SlateBasics.h"
#include "SlateExtras.h"
#include "SceneViewport.h"

#include "SDockTab.h"
#include "JsonObjectConverter.h"
#include "INotificationWidget.h"
#include "SNotificationList.h"
#include "NotificationManager.h"

#include "EditorStyle.h"
#include "Editor.h"
#include "PropertyEditing.h"
#include "FileHelpers.h"

#include "ISessionServicesModule.h"
#include "ISessionInstanceInfo.h"
#include "ISessionInfo.h"
#include "ISessionManager.h"

#include "SLevelViewport.h"

#define LOCTEXT_NAMESPACE "MovieSceneCaptureDialog"

const TCHAR* MovieCaptureSessionName = TEXT("Movie Scene Capture");

DECLARE_DELEGATE_RetVal_OneParam(FText, FOnStartCapture, UMovieSceneCapture*);

class SRenderMovieSceneSettings : public SCompoundWidget, public FGCObject
{
	SLATE_BEGIN_ARGS(SRenderMovieSceneSettings) : _InitialObject(nullptr) {}
		SLATE_EVENT(FOnStartCapture, OnStartCapture)
		SLATE_ARGUMENT(UMovieSceneCapture*, InitialObject)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs)
	{
		FPropertyEditorModule& PropertyEditor = FModuleManager::LoadModuleChecked<FPropertyEditorModule>("PropertyEditor");

		FDetailsViewArgs DetailsViewArgs;
		DetailsViewArgs.bUpdatesFromSelection = false;
		DetailsViewArgs.bLockable = false;
		DetailsViewArgs.NameAreaSettings = FDetailsViewArgs::HideNameArea;
		DetailsViewArgs.ViewIdentifier = "RenderMovieScene";

		DetailView = PropertyEditor.CreateDetailView(DetailsViewArgs);

		OnStartCapture = InArgs._OnStartCapture;

		ChildSlot
		[
			SNew(SVerticalBox)

			+ SVerticalBox::Slot()
			[
				DetailView.ToSharedRef()
			]

			+ SVerticalBox::Slot()
			.AutoHeight()
			[
				SAssignNew(ErrorText, STextBlock)
				.Visibility(EVisibility::Hidden)
			]

			+ SVerticalBox::Slot()
			.AutoHeight()
			.HAlign(HAlign_Right)
			.Padding(5.f)
			[
				SNew(SButton)
				.ContentPadding(FMargin(10, 5))
				.Text(LOCTEXT("Export", "Capture Movie"))
				.OnClicked(this, &SRenderMovieSceneSettings::OnStartClicked)
			]
			
		];

		if (InArgs._InitialObject)
		{
			SetObject(InArgs._InitialObject);
		}
	}

	void SetObject(UMovieSceneCapture* InMovieSceneCapture)
	{
		MovieSceneCapture = InMovieSceneCapture;

		DetailView->SetObject(InMovieSceneCapture);

		ErrorText->SetText(FText());
		ErrorText->SetVisibility(EVisibility::Hidden);
	}

	virtual void AddReferencedObjects( FReferenceCollector& Collector ) override
	{
		Collector.AddReferencedObject(MovieSceneCapture);
	}

private:

	FReply OnStartClicked()
	{
		FText Error;
		if (OnStartCapture.IsBound())
		{
			Error = OnStartCapture.Execute(MovieSceneCapture);
		}

		ErrorText->SetText(Error);
		ErrorText->SetVisibility(Error.IsEmpty() ? EVisibility::Hidden : EVisibility::Visible);

		return FReply::Handled();
	}

	TSharedPtr<IDetailsView> DetailView;
	TSharedPtr<STextBlock> ErrorText;
	FOnStartCapture OnStartCapture;
	UMovieSceneCapture* MovieSceneCapture;
};

enum class ECaptureState
{
	Pending,
	Success,
	Failure
};

DECLARE_DELEGATE_OneParam(FOnCaptureFinished, bool /*bCancelled*/);

class SCaptureMovieNotification : public SCompoundWidget, public INotificationWidget
{
public:
	SLATE_BEGIN_ARGS(SCaptureMovieNotification){}

		SLATE_ATTRIBUTE(ECaptureState, CaptureState)

		SLATE_EVENT(FOnCaptureFinished, OnCaptureFinished)

		SLATE_EVENT(FSimpleDelegate, OnCancel)

		SLATE_ARGUMENT(FString, CapturePath)

	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs)
	{
		CaptureState = InArgs._CaptureState;
		OnCaptureFinished = InArgs._OnCaptureFinished;
		OnCancel = InArgs._OnCancel;

		CachedState = ECaptureState::Pending;

		FString CapturePath = FPaths::ConvertRelativePathToFull(InArgs._CapturePath);
		CapturePath.RemoveFromEnd(TEXT("\\"));

		auto OnBrowseToFolder = [=]{
			FPlatformProcess::ExploreFolder(*CapturePath);
		};

		ChildSlot
		[
			SNew(SBorder)
			.Padding(FMargin(15.0f))
			.BorderImage(FCoreStyle::Get().GetBrush("NotificationList.ItemBackground"))
			[
				SNew(SVerticalBox)

				+ SVerticalBox::Slot()
				.Padding(FMargin(0,0,0,5.0f))
				.HAlign(HAlign_Right)
				.AutoHeight()
				[
					SNew(SHorizontalBox)

					+ SHorizontalBox::Slot()
					.VAlign(VAlign_Center)
					[
						SAssignNew(TextBlock, STextBlock)
						.Font(FCoreStyle::Get().GetFontStyle(TEXT("NotificationList.FontBold")))
						.Text(LOCTEXT("RenderingVideo", "Capturing video"))
					]

					+ SHorizontalBox::Slot()
					.AutoWidth()
					.Padding(FMargin(15.f,0,0,0))
					[
						SAssignNew(Throbber, SThrobber)
					]
				]

				+ SVerticalBox::Slot()
				.AutoHeight()
				.HAlign(HAlign_Right)
				[
					SNew(SHorizontalBox)

					+ SHorizontalBox::Slot()
					.AutoWidth()
					.VAlign(VAlign_Center)
					[
						SAssignNew(Hyperlink, SHyperlink)
						.Visibility(EVisibility::Collapsed)
						.Text(LOCTEXT("OpenFolder", "Open Capture Folder..."))
						.OnNavigate_Lambda(OnBrowseToFolder)
					]

					+ SHorizontalBox::Slot()
					.AutoWidth()
					.VAlign(VAlign_Center)
					[
						SAssignNew(Button, SButton)
						.Text(LOCTEXT("StopButton", "Stop Capture"))
						.OnClicked(this, &SCaptureMovieNotification::ButtonClicked)
					]
				]
			]
		];
	}

	virtual void Tick( const FGeometry& AllottedGeometry, const double InCurrentTime, const float InDeltaTime )
	{
		if (State != SNotificationItem::CS_Pending)
		{
			return;
		}

		ECaptureState StateThisFrame = CaptureState.Get();

		if (CachedState != StateThisFrame)
		{
			CachedState = StateThisFrame;
			
			if (CachedState == ECaptureState::Success)
			{
				TextBlock->SetText(LOCTEXT("CaptureFinished", "Capture Finished"));
				OnCaptureFinished.ExecuteIfBound(true);
			}
			else if (CachedState == ECaptureState::Failure)
			{
				TextBlock->SetText(LOCTEXT("CaptureFailed", "Capture Failed"));
				OnCaptureFinished.ExecuteIfBound(false);
			}
			else
			{
				ensureMsgf(false, TEXT("Cannot move from a finished to a pending state."));
			}
		}
	}

	virtual void OnSetCompletionState(SNotificationItem::ECompletionState InState)
	{
		State = InState;
		if (State != SNotificationItem::CS_Pending)
		{
			Hyperlink->SetVisibility(EVisibility::Visible);
			Throbber->SetVisibility(EVisibility::Collapsed);
			Button->SetVisibility(EVisibility::Collapsed);
		}
	}

	virtual TSharedRef< SWidget > AsWidget()
	{
		return AsShared();
	}

private:

	FReply ButtonClicked()
	{
		if (State == SNotificationItem::CS_Pending)
		{
			OnCancel.ExecuteIfBound();
		}
		return FReply::Handled();
	}
	
private:
	TSharedPtr<SWidget> Button, Throbber, Hyperlink;
	TSharedPtr<STextBlock> TextBlock;
	SNotificationItem::ECompletionState State;

	FSimpleDelegate OnCancel;
	ECaptureState CachedState;
	TAttribute<ECaptureState> CaptureState;
	FOnCaptureFinished OnCaptureFinished;
};

struct FInEditorCapture
{
	FInEditorCapture(UMovieSceneCapture* InCaptureObject, TFunction<void()> InOnStarted)
		: CaptureObject(InCaptureObject)
	{
		ULevelEditorPlaySettings* PlayInEditorSettings = GetMutableDefault<ULevelEditorPlaySettings>();

		bScreenMessagesWereEnabled = GAreScreenMessagesEnabled;
		GAreScreenMessagesEnabled = false;
		OnStarted = InOnStarted;
		FObjectWriter(PlayInEditorSettings, BackedUpPlaySettings);
		OverridePlaySettings(PlayInEditorSettings);

		CaptureObject->AddToRoot();
		CaptureObject->OnCaptureFinished().AddRaw(this, &FInEditorCapture::OnEnd);

		UGameViewportClient::OnViewportCreated().AddRaw(this, &FInEditorCapture::OnStart);
		FEditorDelegates::EndPIE.AddRaw(this, &FInEditorCapture::OnEndPIE);
		
		GEditor->RequestPlaySession(true, nullptr, false);
	}

	void OverridePlaySettings(ULevelEditorPlaySettings* PlayInEditorSettings)
	{
		const FMovieSceneCaptureSettings& Settings = CaptureObject->GetSettings();

		PlayInEditorSettings->NewWindowWidth = Settings.Resolution.ResX;
		PlayInEditorSettings->NewWindowHeight = Settings.Resolution.ResY;
		PlayInEditorSettings->CenterNewWindow = true;
		PlayInEditorSettings->LastExecutedPlayModeType = EPlayModeType::PlayMode_InEditorFloating;

		TSharedRef<SWindow> CustomWindow = SNew(SWindow)
			.Title(LOCTEXT("MovieRenderPreviewTitle", "Movie Render - Preview"))
			.AutoCenter(EAutoCenter::PrimaryWorkArea)
			.UseOSWindowBorder(true)
			.FocusWhenFirstShown(false)
			.ActivateWhenFirstShown(false)
			.HasCloseButton(true)
			.SupportsMaximize(false)
			.SupportsMinimize(true)
			.SizingRule(ESizingRule::FixedSize);

		FSlateApplication::Get().AddWindow(CustomWindow);

		PlayInEditorSettings->CustomPIEWindow = CustomWindow;

		// Reset everything else
		PlayInEditorSettings->GameGetsMouseControl = false;
		PlayInEditorSettings->ShowMouseControlLabel = false;
		PlayInEditorSettings->ViewportGetsHMDControl = false;
		PlayInEditorSettings->EnableSound = false;
		PlayInEditorSettings->bOnlyLoadVisibleLevelsInPIE = false;
		PlayInEditorSettings->bPreferToStreamLevelsInPIE = false;
		PlayInEditorSettings->PIEAlwaysOnTop = false;
		PlayInEditorSettings->DisableStandaloneSound = true;
		PlayInEditorSettings->AdditionalLaunchParameters = TEXT("");
		PlayInEditorSettings->BuildGameBeforeLaunch = EPlayOnBuildMode::PlayOnBuild_Never;
		PlayInEditorSettings->LaunchConfiguration = EPlayOnLaunchConfiguration::LaunchConfig_Default;
		PlayInEditorSettings->SetPlayNetMode(EPlayNetMode::PIE_Standalone);
		PlayInEditorSettings->SetRunUnderOneProcess(true);
		PlayInEditorSettings->SetPlayNetDedicated(false);
		PlayInEditorSettings->SetPlayNumberOfClients(1);
	}

	void OnStart()
	{
		for (const FWorldContext& Context : GEngine->GetWorldContexts())
		{
			if (Context.WorldType == EWorldType::PIE)
			{
				FSlatePlayInEditorInfo* SlatePlayInEditorSession = GEditor->SlatePlayInEditorMap.Find(Context.ContextHandle);
				if (SlatePlayInEditorSession)
				{
					TSharedPtr<SWindow> Window = SlatePlayInEditorSession->SlatePlayInEditorWindow.Pin();

					const FMovieSceneCaptureSettings& Settings = CaptureObject->GetSettings();

					SlatePlayInEditorSession->SlatePlayInEditorWindowViewport->SetViewportSize(Settings.Resolution.ResX,Settings.Resolution.ResY);

					FVector2D PreviewWindowSize(Settings.Resolution.ResX, Settings.Resolution.ResY);

					// Keep scaling down the window size while we're bigger than half the destop width/height
					{
						FDisplayMetrics DisplayMetrics;
						FSlateApplication::Get().GetDisplayMetrics(DisplayMetrics);
						
						while(PreviewWindowSize.X >= DisplayMetrics.PrimaryDisplayWidth*.5f || PreviewWindowSize.Y >= DisplayMetrics.PrimaryDisplayHeight*.5f)
						{
							PreviewWindowSize *= .5f;
						}
					}
					
					Window->Resize(PreviewWindowSize);

					CaptureObject->Initialize(SlatePlayInEditorSession->SlatePlayInEditorWindowViewport, Context.PIEInstance);
					OnStarted();
				}
				return;
			}
		}

		// todo: error?
	}

	void Shutdown()
	{
		FEditorDelegates::EndPIE.RemoveAll(this);
		UGameViewportClient::OnViewportCreated().RemoveAll(this);
		CaptureObject->OnCaptureFinished().RemoveAll(this);

		GAreScreenMessagesEnabled = bScreenMessagesWereEnabled;

		FObjectReader(GetMutableDefault<ULevelEditorPlaySettings>(), BackedUpPlaySettings);

		CaptureObject->Close();
		CaptureObject->RemoveFromRoot();

	}
	void OnEndPIE(bool bIsSimulating)
	{
		Shutdown();
		delete this;
	}

	void OnEnd()
	{
		Shutdown();
		delete this;

		GEditor->RequestEndPlayMap();
	}

	TFunction<void()> OnStarted;
	bool bScreenMessagesWereEnabled;
	TArray<uint8> BackedUpPlaySettings;
	UMovieSceneCapture* CaptureObject;
};

class FMovieSceneCaptureDialogModule : public IMovieSceneCaptureDialogModule
{
	virtual void OpenDialog(const TSharedRef<FTabManager>& TabManager, UMovieSceneCapture* CaptureObject) override
	{
		// Ensure the session services module is loaded otherwise we won't necessarily receive status updates from the movie capture session
		FModuleManager::Get().LoadModuleChecked<ISessionServicesModule>("SessionServices").GetSessionManager();

		TSharedPtr<SWindow> ExistingWindow = CaptureSettingsWindow.Pin();
		if (ExistingWindow.IsValid())
		{
			ExistingWindow->BringToFront();
		}
		else
		{
			ExistingWindow = SNew(SWindow)
				.Title( LOCTEXT("RenderMovieSettingsTitle", "Render Movie Settings") )
				.HasCloseButton(true)
				.SupportsMaximize(false)
				.SupportsMinimize(false)
				.ClientSize(FVector2D(500, 700));

			TSharedPtr<SDockTab> OwnerTab = TabManager->GetOwnerTab();
			TSharedPtr<SWindow> RootWindow = OwnerTab.IsValid() ? OwnerTab->GetParentWindow() : TSharedPtr<SWindow>();
			if(RootWindow.IsValid())
			{
				FSlateApplication::Get().AddWindowAsNativeChild(ExistingWindow.ToSharedRef(), RootWindow.ToSharedRef());
			}
			else
			{
				FSlateApplication::Get().AddWindow(ExistingWindow.ToSharedRef());
			}
		}

		ExistingWindow->SetContent(
			SNew(SRenderMovieSceneSettings)
			.InitialObject(CaptureObject)
			.OnStartCapture_Raw(this, &FMovieSceneCaptureDialogModule::OnStartCapture)
		);

		CaptureSettingsWindow = ExistingWindow;
	}

	void OnCaptureFinished(bool bSuccess)
	{
		if (bSuccess)
		{
			InProgressCaptureNotification->SetCompletionState(SNotificationItem::CS_Success);
		}
		else
		{
			// todo: error to message log
			InProgressCaptureNotification->SetCompletionState(SNotificationItem::CS_Fail);
		}

		InProgressCaptureNotification->ExpireAndFadeout();
		InProgressCaptureNotification = nullptr;
	}

	FText OnStartCapture(UMovieSceneCapture* CaptureObject)
	{
		FString MapNameToLoad;
		if( CaptureObject->Settings.bCreateTemporaryCopiesOfLevels )
		{
			TArray<FString> SavedMapNames;
			GEditor->SaveWorldForPlay(SavedMapNames);

			if (SavedMapNames.Num() == 0)
			{
				return LOCTEXT("CouldNotSaveMap", "Could not save map for movie capture.");
			}

			MapNameToLoad = SavedMapNames[ 0 ];
		}
		else
		{
			// Prompt the user to save their changes so that they'll be in the movie, since we're not saving temporary copies of the level.
			bool bPromptUserToSave = true;
			bool bSaveMapPackages = true;
			bool bSaveContentPackages = true;
			if( !FEditorFileUtils::SaveDirtyPackages( bPromptUserToSave, bSaveMapPackages, bSaveContentPackages ) )
			{
				return LOCTEXT( "UserCancelled", "Capturing was cancelled from the save dialog." );
			}

			const FString WorldPackageName = GWorld->GetOutermost()->GetName();
			MapNameToLoad = WorldPackageName;
		}

		// Allow the game mode to be overridden
		if( CaptureObject->Settings.GameModeOverride != nullptr )
		{
			const FString GameModeName = CaptureObject->Settings.GameModeOverride->GetPathName();
			MapNameToLoad += FString::Printf( TEXT( "?game=%s" ), *GameModeName );
		}

		if (InProgressCaptureNotification.IsValid())
		{
			return LOCTEXT("AlreadyCapturing", "There is already a movie scene capture process open. Please close it and try again.");
		}

		CaptureObject->SaveConfig();
		if (CaptureObject->ProtocolSettings)
		{
			CaptureObject->ProtocolSettings->SaveConfig();
		}

		return CaptureObject->bUseSeparateProcess ? CaptureInNewProcess(CaptureObject, MapNameToLoad) : CaptureInEditor(CaptureObject, MapNameToLoad);
	}

	FText CaptureInEditor(UMovieSceneCapture* CaptureObject, const FString& MapNameToLoad)
	{
		auto GetCaptureStatus = [=]{
			for (const FWorldContext& Context : GEngine->GetWorldContexts())
			{
				if (Context.WorldType == EWorldType::PIE)
				{
					return ECaptureState::Pending;
				}
			}

			return ECaptureState::Success;
		};

		auto OnCaptureStarted = [=]{

			FNotificationInfo Info(
				SNew(SCaptureMovieNotification)
				.CaptureState_Lambda(GetCaptureStatus)
				.CapturePath(CaptureObject->Settings.OutputDirectory.Path)
				.OnCaptureFinished_Raw(this, &FMovieSceneCaptureDialogModule::OnCaptureFinished)
				.OnCancel_Lambda([]{
					GEditor->RequestEndPlayMap();
				})
			);
			Info.bFireAndForget = false;
			Info.ExpireDuration = 5.f;
			InProgressCaptureNotification = FSlateNotificationManager::Get().AddNotification(Info);
			InProgressCaptureNotification->SetCompletionState(SNotificationItem::CS_Pending);
		};

		// deliberately 'leak' the object, since it owns itself
		new FInEditorCapture(CaptureObject, OnCaptureStarted);

		return FText();
	}

	FText CaptureInNewProcess(UMovieSceneCapture* CaptureObject, const FString& MapNameToLoad)
	{
		// Save out the capture manifest to json
		FString Filename = FPaths::GameSavedDir() / TEXT("MovieSceneCapture/Manifest.json");

		TSharedRef<FJsonObject> Object = MakeShareable(new FJsonObject);
		if (FJsonObjectConverter::UStructToJsonObject(CaptureObject->GetClass(), CaptureObject, Object, 0, 0))
		{
			TSharedRef<FJsonObject> RootObject = MakeShareable(new FJsonObject);
			RootObject->SetField(TEXT("Type"), MakeShareable(new FJsonValueString(CaptureObject->GetClass()->GetPathName())));
			RootObject->SetField(TEXT("Data"), MakeShareable(new FJsonValueObject(Object)));

			if (CaptureObject->ProtocolSettings)
			{
				RootObject->SetField(TEXT("ProtocolType"), MakeShareable(new FJsonValueString(CaptureObject->ProtocolSettings->GetClass()->GetPathName())));
				TSharedRef<FJsonObject> ProtocolDataObject = MakeShareable(new FJsonObject);
				if (FJsonObjectConverter::UStructToJsonObject(CaptureObject->ProtocolSettings->GetClass(), CaptureObject->ProtocolSettings, ProtocolDataObject, 0, 0))
				{
					RootObject->SetField(TEXT("ProtocolData"), MakeShareable(new FJsonValueObject(ProtocolDataObject)));
				}
			}

			FString Json;
			TSharedRef<TJsonWriter<> > JsonWriter = TJsonWriterFactory<>::Create(&Json, 0);
			if (FJsonSerializer::Serialize( RootObject, JsonWriter ))
			{
				FFileHelper::SaveStringToFile(Json, *Filename);
			}
		}
		else
		{
			return LOCTEXT("UnableToSaveCaptureManifest", "Unable to save capture manifest");
		}

		FString EditorCommandLine = FString::Printf(TEXT("%s -MovieSceneCaptureManifest=\"%s\" -game"), *MapNameToLoad, *Filename);

		if( CaptureObject->Settings.bCreateTemporaryCopiesOfLevels )
		{
			// the PIEVIACONSOLE parameter tells UGameEngine to add the auto-save dir to the paths array and repopulate the package file cache
			// this is needed in order to support streaming levels as the streaming level packages will be loaded only when needed (thus
			// their package names need to be findable by the package file caching system)
			// (we add to EditorCommandLine because the URL is ignored by WindowsTools)
			EditorCommandLine.Append( TEXT( " -PIEVIACONSOLE" ) );
		}

		// Spit out any additional, user-supplied command line args
		if (!CaptureObject->AdditionalCommandLineArguments.IsEmpty())
		{
			EditorCommandLine.AppendChar(' ');
			EditorCommandLine.Append(CaptureObject->AdditionalCommandLineArguments);
		}
		
		// Spit out any inherited command line args
		if (!CaptureObject->InheritedCommandLineArguments.IsEmpty())
		{
			EditorCommandLine.AppendChar(' ');
			EditorCommandLine.Append(CaptureObject->InheritedCommandLineArguments);
		}

		// Disable texture streaming if necessary
		if (!CaptureObject->Settings.bEnableTextureStreaming)
		{
			EditorCommandLine.Append(TEXT(" -NoTextureStreaming"));
		}
		
		// Set the game resolution
		EditorCommandLine += FString::Printf(TEXT(" -ResX=%d -ResY=%d"), CaptureObject->Settings.Resolution.ResX, CaptureObject->Settings.Resolution.ResY);

		// Ensure game session is correctly set up 
		EditorCommandLine += FString::Printf(TEXT(" -messaging -SessionName=\"%s\""), MovieCaptureSessionName);

		FString Params;
		if (FPaths::IsProjectFilePathSet())
		{
			Params = FString::Printf(TEXT("\"%s\" %s %s"), *FPaths::GetProjectFilePath(), *EditorCommandLine, *FCommandLine::GetSubprocessCommandline());
		}
		else
		{
			Params = FString::Printf(TEXT("%s %s %s"), FApp::GetGameName(), *EditorCommandLine, *FCommandLine::GetSubprocessCommandline());
		}

		FString GamePath = FPlatformProcess::GenerateApplicationPath(FApp::GetName(), FApp::GetBuildConfiguration());
		FProcHandle ProcessHandle = FPlatformProcess::CreateProc(*GamePath, *Params, true, false, false, nullptr, 0, nullptr, nullptr);

		// @todo: progress reporting, UI feedback
		if (ProcessHandle.IsValid())
		{
			TSharedRef<FProcHandle> SharedProcHandle = MakeShareable(new FProcHandle(ProcessHandle));
			auto GetCaptureStatus = [=]{
				if (!FPlatformProcess::IsProcRunning(*SharedProcHandle))
				{
					int32 RetCode = 0;
					FPlatformProcess::GetProcReturnCode(*SharedProcHandle, &RetCode);
					return RetCode == 0 ? ECaptureState::Success : ECaptureState::Failure;
				}
				else
				{
					return ECaptureState::Pending;
				}
			};

			auto OnCancel = [=]{
				bool bFoundInstance = false;

				// Attempt to send a remote command to gracefully terminate the process
				ISessionServicesModule& SessionServices = FModuleManager::Get().LoadModuleChecked<ISessionServicesModule>("SessionServices");
				TSharedRef<ISessionManager> SessionManager = SessionServices.GetSessionManager();

				TArray<TSharedPtr<ISessionInfo>> Sessions;
				SessionManager->GetSessions(Sessions);

				for (const TSharedPtr<ISessionInfo>& Session : Sessions)
				{
					if (Session->GetSessionName() == MovieCaptureSessionName)
					{
						TArray<TSharedPtr<ISessionInstanceInfo>> Instances;
						Session->GetInstances(Instances);

						for (const TSharedPtr<ISessionInstanceInfo>& Instance : Instances)
						{
							Instance->ExecuteCommand("exit");
							bFoundInstance = true;
						}
					}
				}

				if (!bFoundInstance)
				{
					FPlatformProcess::TerminateProc(*SharedProcHandle);
				}
			};
			FNotificationInfo Info(
				SNew(SCaptureMovieNotification)
				.CaptureState_Lambda(GetCaptureStatus)
				.CapturePath(CaptureObject->Settings.OutputDirectory.Path)
				.OnCaptureFinished_Raw(this, &FMovieSceneCaptureDialogModule::OnCaptureFinished)
				.OnCancel_Lambda(OnCancel)
			);
			Info.bFireAndForget = false;
			Info.ExpireDuration = 5.f;
			InProgressCaptureNotification = FSlateNotificationManager::Get().AddNotification(Info);
			InProgressCaptureNotification->SetCompletionState(SNotificationItem::CS_Pending);
		}

		return FText();
	}
private:
	/** */
	TWeakPtr<SWindow> CaptureSettingsWindow;
	TSharedPtr<SNotificationItem> InProgressCaptureNotification;
};

IMPLEMENT_MODULE( FMovieSceneCaptureDialogModule, MovieSceneCaptureDialog )

#undef LOCTEXT_NAMESPACE