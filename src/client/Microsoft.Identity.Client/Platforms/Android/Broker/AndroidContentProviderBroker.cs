﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using AndroidNative = Android;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Internal.Broker;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Json.Linq;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Http;
using System.Net;
using Android.OS;
using System.Linq;
using AndroidUri = Android.Net.Uri;
using Android.Accounts;
using Microsoft.Identity.Client.Platforms.Android.Broker.Requests;

namespace Microsoft.Identity.Client.Platforms.Android.Broker
{
    [AndroidNative.Runtime.Preserve(AllMembers = true)]
    internal class AndroidContentProviderBroker : IBroker
    {
        private static MsalTokenResponse s_androidBrokerTokenResponse = null;
        //Since the correlation ID is not returned from the broker response, it must be stored at the beginning of the authentication call and re-injected into the response at the end.
        private static string s_correlationId;
        private readonly AndroidBrokerHelper _brokerHelper;
        private readonly ICoreLogger _logger;
        private Activity _parentActivity;
        private string _negotiatedBrokerProtocalKey = String.Empty;

        public AndroidContentProviderBroker(CoreUIParent uiParent, ICoreLogger logger)
        {
            _parentActivity = uiParent?.Activity;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            AuthenticationContinuationHelper.LastRequestLogger = _logger;
            _brokerHelper = new AndroidBrokerHelper(Application.Context, logger);
        }

        public bool IsBrokerInstalledAndInvokable()
        {
            using (_logger.LogMethodDuration())
            {
                bool canInvoke = _brokerHelper.CanSwitchToBroker();
                _logger.Verbose("Can invoke broker? " + canInvoke);

                return canInvoke;
            }
        }

        ///Check if the network is available.
        private void CheckPowerOptimizationStatus()
        {
            checkPackageForPowerOptimization(Application.Context.PackageName);
        }

        private void checkPackageForPowerOptimization(string package)
        {
            var powerManager = PowerManager.FromContext(Application.Context);

            //Power optimization checking was added in API 23
            if ((int)Build.VERSION.SdkInt >= (int)BuildVersionCodes.M &&
                powerManager.IsDeviceIdleMode &&
                !powerManager.IsIgnoringBatteryOptimizations(package))
            {
                _logger.Error("Power optimization detected for the application: " + package + " and the device is in doze mode or the app is in standby. \n" +
                    "Please disable power optimizations for this application to authenticate.");
            }
        }

        public async Task<MsalTokenResponse> AcquireTokenInteractiveAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenInteractiveParameters acquireTokenInteractiveParameters)
        {
            CheckPowerOptimizationStatus();

            InitiateBrokerHandshakeAsync();

            var brokerRequest = PrepareInteractiveBrokerRequest(authenticationRequestParameters, acquireTokenInteractiveParameters);

            return await PerformAcquireTokenInteractiveAsync(brokerRequest).ConfigureAwait(false);
        }

        private async Task<MsalTokenResponse> PerformAcquireTokenInteractiveAsync(BrokerRequest brokerRequest)
        {
            try
            {
                await Task.Run(() => AcquireTokenInteractiveViaContentProviderAsync(brokerRequest)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorPiiWithPrefix(ex, "Android broker interactive invocation failed. ");
                HandleBrokerOperationError(ex);
            }

            using (_logger.LogBlockDuration("Waiting for Android broker response. "))
            {
                await AndroidBrokerHelper.ReadyForResponse.WaitAsync().ConfigureAwait(false);
                return s_androidBrokerTokenResponse;
            }
        }

        private BrokerRequest PrepareInteractiveBrokerRequest(AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenInteractiveParameters acquireTokenInteractiveParameters)
        {
            s_androidBrokerTokenResponse = null;

            BrokerRequest brokerRequest = BrokerRequest.FromInteractiveParameters(
                authenticationRequestParameters, acquireTokenInteractiveParameters);

            // There can only be 1 broker request at a time so keep track of the correlation id
            s_correlationId = brokerRequest.CorrelationId;

            return brokerRequest;
        }

        public async void InitiateBrokerHandshakeAsync()
        {
            using (_logger.LogMethodDuration())
            {
                try
                {
                    Bundle HandshakeBundleResult = await GetHandshakeBundleResultFromBrokerAsync().ConfigureAwait(false);

                    _negotiatedBrokerProtocalKey = _brokerHelper.GetProtocalKeyFromHandshakeResult(HandshakeBundleResult);
                }
                catch (Exception e)
                {
                    throw e;
                }
            }
        }

        private async Task<Bundle> GetHandshakeBundleResultFromBrokerAsync()
        {
            var bundle = _brokerHelper.GetHandshakeOperationBundle();
            var OperationBundleJSON = _brokerHelper.SearializeBundleToJSON(bundle);
            return await PerformContentResolverOperationAsync(OperationBundleJSON, BrokerConstants.ContentProviderHelloOperation).ConfigureAwait(false);
        }

        private async Task<Bundle> PerformContentResolverOperationAsync(string operation, string OperationParameters)
        {
            ContentResolver resolver = GetContentResolver();

            var cursor = await Task.Run(() => resolver.Query(AndroidUri.Parse(GetContentProviderURIForOperation(operation)),
                                                            new[] { _negotiatedBrokerProtocalKey },
                                                            OperationParameters,
                                                            null,
                                                            null)).ConfigureAwait(false);

            if (cursor == null)
            {
                _logger.Error("MSAL is unable to communicate to the broker using MSAL.");
                throw new MsalClientException("broker_error");
            }

            return cursor.Extras;
        }

        private ContentResolver GetContentResolver()
        {
            if (_parentActivity == null)
            {
                return Application.Context.ContentResolver;
            }
            else
            {
                return _parentActivity.ContentResolver;
            }
        }

        public string GetContentProviderURIForOperation(string operation)
        {
            return "content://com.azure.authenticator.microsoft.identity.broker" + operation;
        }

        public async Task<MsalTokenResponse> AcquireTokenSilentAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenSilentParameters acquireTokenSilentParameters)
        {
            CheckPowerOptimizationStatus();

            BrokerRequest brokerRequest = BrokerRequest.FromSilentParameters(
                authenticationRequestParameters, acquireTokenSilentParameters);

            try
            {
                InitiateBrokerHandshakeAsync();
                return await AcquireTokenSilentViaBrokerAsync(brokerRequest).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorPiiWithPrefix(ex, "Android broker silent invocation failed. ");
                HandleBrokerOperationError(ex);
                throw;
            }
        }

        private void AcquireTokenInteractiveViaContentProviderAsync(BrokerRequest brokerRequest)
        {
            using (_logger.LogMethodDuration())
            {
                _logger.Verbose("Starting Android Broker interactive authentication. ");

                Bundle bundleResult = GetAcquireTokenInteractiveOperationBundle();

                var interactiveIntent = CreateInteractiveBrokerIntent(brokerRequest, bundleResult);

                _brokerHelper.LaunchInteractiveActivity(_parentActivity, interactiveIntent);
            }
        }

        private Intent CreateInteractiveBrokerIntent(BrokerRequest brokerRequest, Bundle bundleResult)
        {
            string packageName = bundleResult.GetString("broker.package.name");
            string className = bundleResult.GetString("broker.activity.name");
            string uid = bundleResult.GetString("caller.info.uid");

            Intent brokerIntent = new Intent();
            brokerIntent.SetPackage(packageName);
            brokerIntent.SetClassName(
                    packageName,
                    className
            );

            brokerIntent.PutExtras(bundleResult);
            brokerIntent.PutExtra(BrokerConstants.NegotiatedBPVersionKey, _brokerHelper.NegotiatedBrokerProtocalKey);

            var interactiveIntent = brokerIntent.PutExtras(_brokerHelper.GetInteractiveBrokerBundle(brokerRequest));
            interactiveIntent.PutExtra(BrokerConstants.CallerInfoUID, Binder.CallingUid);

            return interactiveIntent;
        }

        private Bundle GetAcquireTokenInteractiveOperationBundle()
        {
            return PerformContentResolverOperationAsync(BrokerConstants.ContentProviderInteractiveOperation, null).Result;
        }

        private async Task<MsalTokenResponse> AcquireTokenSilentViaBrokerAsync(BrokerRequest brokerRequest)
        {
            // Don't send silent background request if account information is not provided

            using (_logger.LogMethodDuration())
            {
                _logger.Verbose("User is specified for silent token request. Starting silent Android broker request. ");

                brokerRequest = await UpdateRequestWithAccountInfoAsync(brokerRequest).ConfigureAwait(false);

                string silentResult = await PerformAcquireTokenSilentFromBrokerAsync(brokerRequest).ConfigureAwait(false);
                return _brokerHelper.HandleSilentAuthenticationResult(silentResult, s_correlationId);
            }
        }

        public async Task<string> PerformAcquireTokenSilentFromBrokerAsync(BrokerRequest brokerRequest)
        {
            Bundle silentOperationBundle = _brokerHelper.CreateSilentBrokerBundle(brokerRequest);
            var OperationBundleJSON = _brokerHelper.SearializeBundleToJSON(silentOperationBundle);
            var SilentOperationBundle = await PerformContentResolverOperationAsync(OperationBundleJSON, BrokerConstants.ContentProviderHelloOperation).ConfigureAwait(false);
            return _brokerHelper.GetSilentResultFromBundle(SilentOperationBundle);
        }

        /// <summary>
        /// This method is only used for Silent authentication requests so that we can check to see if an account exists in the account manager before
        /// sending the silent request to the broker. 
        /// </summary>
        public async Task<BrokerRequest> UpdateRequestWithAccountInfoAsync(BrokerRequest brokerRequest)
        {
            var accounts = await GetBrokerAccountsAsync(brokerRequest).ConfigureAwait(false);

            return _brokerHelper.UpdateBrokerRequestWithAccountInformation(accounts, brokerRequest);
        }

        private async Task<string> GetBrokerAccountsAsync(BrokerRequest brokerRequest)
        {
            var getAccountsBundle = _brokerHelper.CreateBrokerAccountBundle(brokerRequest);
            var OperationBundleJSON = _brokerHelper.SearializeBundleToJSON(getAccountsBundle);
            var bundleResult = await PerformContentResolverOperationAsync(OperationBundleJSON, BrokerConstants.ContentProviderGetAccountsOperation).ConfigureAwait(false);

            return bundleResult?.GetString(BrokerConstants.BrokerAccounts);
        }

        public void HandleInstallUrl(string appLink)
        {
            _logger.Info("Android Broker - Starting ActionView activity to " + appLink);
            _parentActivity.StartActivity(new Intent(Intent.ActionView, AndroidNative.Net.Uri.Parse(appLink)));

            throw new MsalClientException(
                MsalError.BrokerApplicationRequired,
                MsalErrorMessage.BrokerApplicationRequired);
        }

        internal static void SetBrokerResult(Intent data, int resultCode, ICoreLogger unreliableLogger)
        {
            try
            {
                if (data == null)
                {
                    unreliableLogger?.Info("Data is null, stopping. ");
                    return;
                }

                switch (resultCode)
                {
                    case (int)BrokerResponseCode.ResponseReceived:
                        unreliableLogger?.Info("Response received, decoding... ");

                        s_androidBrokerTokenResponse =
                            MsalTokenResponse.CreateFromAndroidBrokerResponse(
                                data.GetStringExtra(BrokerConstants.BrokerResultV2),
                                s_correlationId);
                        break;
                    case (int)BrokerResponseCode.UserCancelled:
                        unreliableLogger?.Info("Response received - user cancelled. ");

                        s_androidBrokerTokenResponse = new MsalTokenResponse
                        {
                            Error = MsalError.AuthenticationCanceledError,
                            ErrorDescription = MsalErrorMessage.AuthenticationCanceled,
                        };
                        break;
                    case (int)BrokerResponseCode.BrowserCodeError:
                        unreliableLogger?.Info("Response received - error. ");

                        dynamic errorResult = JObject.Parse(data.GetStringExtra(BrokerConstants.BrokerResultV2));
                        string error = null;
                        string errorDescription = null;

                        if (errorResult != null)
                        {
                            error = errorResult[BrokerResponseConst.BrokerErrorCode]?.ToString();
                            errorDescription = errorResult[BrokerResponseConst.BrokerErrorMessage]?.ToString();

                            unreliableLogger?.Error($"error: {error} errorDescription {errorDescription}. ");
                        }
                        else
                        {
                            error = BrokerConstants.BrokerUnknownErrorCode;
                            errorDescription = "Error Code received, but no error could be extracted. ";
                            unreliableLogger?.Error("Error response received, but not error could be extracted. ");
                        }

                        var httpResponse = new HttpResponse();
                        //TODO: figure out how to get status code properly deserialized from JObject
                        httpResponse.Body = errorResult[BrokerResponseConst.BrokerHttpBody]?.ToString();

                        s_androidBrokerTokenResponse = new MsalTokenResponse
                        {
                            Error = error,
                            ErrorDescription = errorDescription,
                            SubError = errorResult[BrokerResponseConst.BrokerSubError],
                            HttpResponse = httpResponse,
                            CorrelationId = s_correlationId
                        };
                        break;
                    default:
                        unreliableLogger?.Error("Unknown broker response. ");
                        s_androidBrokerTokenResponse = new MsalTokenResponse
                        {
                            Error = BrokerConstants.BrokerUnknownErrorCode,
                            ErrorDescription = "Broker result not returned from android broker. ",
                            CorrelationId = s_correlationId
                        };
                        break;
                }
            }
            finally
            {
                AndroidBrokerHelper.ReadyForResponse.Release();
            }
        }

        public async Task<IEnumerable<IAccount>> GetAccountsAsync(string clientID, string redirectUri)
        {
            using (_logger.LogMethodDuration())
            {
                if (!IsBrokerInstalledAndInvokable())
                {
                    _logger.Warning("Android broker is either not installed or is not reachable so no accounts will be returned. ");
                    return new List<IAccount>();
                }

                BrokerRequest brokerRequest = new BrokerRequest() { ClientId = clientID, RedirectUri = new Uri(redirectUri) };

                try
                {
                    InitiateBrokerHandshakeAsync();
                    var accounts = await GetBrokerAccountsAsync(brokerRequest).ConfigureAwait(false);
                    return _brokerHelper.GetBrokerAccountsInAccountManager(accounts);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to get Android broker accounts from the broker. ");
                    HandleBrokerOperationError(ex);
                    throw;
                }
            }
        }

        public async Task RemoveAccountAsync(IApplicationConfiguration applicationConfiguration, IAccount account)
        {
            using (_logger.LogMethodDuration())
            {
                if (!IsBrokerInstalledAndInvokable())
                {
                    _logger.Warning("Android broker is either not installed or not reachable so no accounts will be removed. ");
                    return;
                }

                try
                {
                    await _brokerHelper.InitiateBrokerHandshakeAsync(_parentActivity).ConfigureAwait(false);
                    _brokerHelper.RemoveBrokerAccountInAccountManager(applicationConfiguration.ClientId, account);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to remove Android broker account from the broker. ");
                    HandleBrokerOperationError(ex);
                    throw;
                }
            }
        }

        private void HandleBrokerOperationError(Exception ex)
        {
            _logger.Error(ex.Message);
            if (ex is MsalException)
                throw ex;
            else
                throw new MsalClientException(MsalError.AndroidBrokerOperationFailed, ex.Message, ex);
        }

        /// <summary>
        /// Android Broker does not support logging in a "default" user.
        /// </summary>
        public Task<MsalTokenResponse> AcquireTokenSilentDefaultUserAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenSilentParameters acquireTokenSilentParameters)
        {
            throw new MsalUiRequiredException(
                       MsalError.CurrentBrokerAccount,
                       MsalErrorMessage.MsalUiRequiredMessage,
                       null,
                       UiRequiredExceptionClassification.AcquireTokenSilentFailed);
        }
    }
}
