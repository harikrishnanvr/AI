﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Luis;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Solutions.Dialogs;
using Microsoft.Bot.Solutions.Resources;
using Microsoft.Bot.Solutions.Skills;
using TestSkill.Dialogs.Main.Resources;
using TestSkill.Dialogs.Sample;
using TestSkill.Dialogs.Shared.DialogOptions;
using TestSkill.Dialogs.Shared.Resources;
using TestSkill.ServiceClients;

namespace TestSkill.Dialogs.Main
{
    public class MainDialog : RouterDialog
    {
        private bool _skillMode;
        private SkillConfigurationBase _services;
        private UserState _userState;
        private ConversationState _conversationState;
        private IServiceManager _serviceManager;
        private IStatePropertyAccessor<SkillConversationState> _conversationStateAccessor;
        private IStatePropertyAccessor<SkillUserState> _userStateAccessor;
        private ResponseTemplateManager _responseManager;

        public MainDialog(
            SkillConfigurationBase services,
            ResponseTemplateManager responseManager,
            ConversationState conversationState,
            UserState userState,
            IBotTelemetryClient telemetryClient,
            IServiceManager serviceManager,
            bool skillMode)
            : base(nameof(MainDialog), telemetryClient)
        {
            _skillMode = skillMode;
            _services = services;
            _responseManager = responseManager;
            _conversationState = conversationState;
            _userState = userState;
            _serviceManager = serviceManager;
            TelemetryClient = telemetryClient;

            // Initialize state accessor
            _conversationStateAccessor = _conversationState.CreateProperty<SkillConversationState>(nameof(SkillConversationState));
            _userStateAccessor = _userState.CreateProperty<SkillUserState>(nameof(SkillUserState));

            // Register dialogs
            RegisterDialogs();
        }

        protected override async Task OnStartAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!_skillMode)
            {
                // send a greeting if we're in local mode
                var message = _responseManager.GetResponse(MainResponses.WelcomeMessage, dc.Context.Activity.Locale);
                await dc.Context.SendActivityAsync(message);
            }
        }

        protected override async Task RouteAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await _conversationStateAccessor.GetAsync(dc.Context, () => new SkillConversationState());

            // get current activity locale
            var locale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var localeConfig = _services.LocaleConfigurations[locale];

            // Get skill LUIS model from configuration
            localeConfig.LuisServices.TryGetValue("TestSkill", out var luisService);

            if (luisService == null)
            {
                throw new Exception("The specified LUIS Model could not be found in your Bot Services configuration.");
            }
            else
            {
                var skillOptions = new SkillTemplateDialogOptions
                {
                    SkillMode = _skillMode,
                };

                var result = await luisService.RecognizeAsync<TestSkillLU>(dc.Context, CancellationToken.None);
                var intent = result?.TopIntent().intent;

                switch (intent)
                {
                    case TestSkillLU.Intent.Sample:
                        {
                            await dc.BeginDialogAsync(nameof(SampleDialog), skillOptions);
                            break;
                        }

                    case TestSkillLU.Intent.None:
                        {
                            // No intent was identified, send confused message
                            var response = _responseManager.GetResponse(SharedResponses.DidntUnderstandMessage, dc.Context.Activity.Locale);
                            await dc.Context.SendActivityAsync(response);

                            if (_skillMode)
                            {
                                await CompleteAsync(dc);
                            }

                            break;
                        }

                    default:
                        {
                            // intent was identified but not yet implemented
                            var response = _responseManager.GetResponse(MainResponses.FeatureNotAvailable, dc.Context.Activity.Locale);
                            await dc.Context.SendActivityAsync(response);

                            if (_skillMode)
                            {
                                await CompleteAsync(dc);
                            }

                            break;
                        }
                }
            }
        }

        protected override async Task CompleteAsync(DialogContext dc, DialogTurnResult result = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_skillMode)
            {
                var response = dc.Context.Activity.CreateReply();
                response.Type = ActivityTypes.EndOfConversation;

                await dc.Context.SendActivityAsync(response);
            }

            // End active dialog
            await dc.EndDialogAsync(result);
        }

        protected override async Task OnEventAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            switch (dc.Context.Activity.Name)
            {
                case Events.SkillBeginEvent:
                    {
                        var state = await _conversationStateAccessor.GetAsync(dc.Context, () => new SkillConversationState());

                        if (dc.Context.Activity.Value is Dictionary<string, object> userData)
                        {
                            // Capture user data from event if needed
                        }

                        break;
                    }

                case Events.TokenResponseEvent:
                    {
                        // Auth dialog completion
                        var result = await dc.ContinueDialogAsync();

                        // If the dialog completed when we sent the token, end the skill conversation
                        if (result.Status != DialogTurnStatus.Waiting)
                        {
                            var response = dc.Context.Activity.CreateReply();
                            response.Type = ActivityTypes.EndOfConversation;

                            await dc.Context.SendActivityAsync(response);
                        }

                        break;
                    }
            }
        }

        protected override async Task<InterruptionAction> OnInterruptDialogAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = InterruptionAction.NoAction;

            if (dc.Context.Activity.Type == ActivityTypes.Message)
            {
                // get current activity locale
                var locale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var localeConfig = _services.LocaleConfigurations[locale];

                // check general luis intent
                localeConfig.LuisServices.TryGetValue("general", out var luisService);

                if (luisService == null)
                {
                    throw new Exception("The specified LUIS Model could not be found in your Skill configuration.");
                }
                else
                {
                    var luisResult = await luisService.RecognizeAsync<General>(dc.Context, cancellationToken);
                    var topIntent = luisResult.TopIntent().intent;

                    switch (topIntent)
                    {
                        case General.Intent.Cancel:
                            {
                                result = await OnCancel(dc);
                                break;
                            }

                        case General.Intent.Help:
                            {
                                result = await OnHelp(dc);
                                break;
                            }

                        case General.Intent.Logout:
                            {
                                result = await OnLogout(dc);
                                break;
                            }
                    }
                }
            }

            return result;
        }

        private async Task<InterruptionAction> OnCancel(DialogContext dc)
        {
            var response = _responseManager.GetResponse(MainResponses.CancelMessage, dc.Context.Activity.Locale);
            await dc.Context.SendActivityAsync(response);

            await CompleteAsync(dc);
            await dc.CancelAllDialogsAsync();
            return InterruptionAction.StartedDialog;
        }

        private async Task<InterruptionAction> OnHelp(DialogContext dc)
        {
            var response = _responseManager.GetResponse(MainResponses.HelpMessage, dc.Context.Activity.Locale);
            await dc.Context.SendActivityAsync(response);
            return InterruptionAction.MessageSentToUser;
        }

        private async Task<InterruptionAction> OnLogout(DialogContext dc)
        {
            BotFrameworkAdapter adapter;
            var supported = dc.Context.Adapter is BotFrameworkAdapter;
            if (!supported)
            {
                throw new InvalidOperationException("OAuthPrompt.SignOutUser(): not supported by the current adapter");
            }
            else
            {
                adapter = (BotFrameworkAdapter)dc.Context.Adapter;
            }

            await dc.CancelAllDialogsAsync();

            // Sign out user
            var tokens = await adapter.GetTokenStatusAsync(dc.Context, dc.Context.Activity.From.Id);
            foreach (var token in tokens)
            {
                await adapter.SignOutUserAsync(dc.Context, token.ConnectionName);
            }

            var response = _responseManager.GetResponse(MainResponses.LogOut, dc.Context.Activity.Locale);
            await dc.Context.SendActivityAsync(response);
            return InterruptionAction.StartedDialog;
        }

        private void RegisterDialogs()
        {
            AddDialog(new SampleDialog(_services, _responseManager, _conversationStateAccessor, _userStateAccessor, _serviceManager, TelemetryClient));
        }

        private class Events
        {
            public const string TokenResponseEvent = "tokens/response";
            public const string SkillBeginEvent = "skillBegin";
        }
    }
}