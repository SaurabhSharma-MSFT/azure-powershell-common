﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using Microsoft.Azure.Commands.Common.Authentication;
using Microsoft.Azure.Commands.Common.Authentication.Abstractions;
using Microsoft.Azure.PowerShell.Common.Config;
using Microsoft.Azure.PowerShell.Common.Share.Survey;
using Microsoft.Azure.ServiceManagement.Common.Models;
using Microsoft.WindowsAzure.Commands.Common;
using Microsoft.WindowsAzure.Commands.Common.CustomAttributes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.WindowsAzure.Commands.Utilities.Common
{
    /// <summary>
    /// Represents base class for all Azure cmdlets.
    /// </summary>
    public abstract class AzurePSCmdlet : PSCmdlet, IDisposable
    {
        private const string PSVERSION = "PSVersion";
        private const string DEFAULT_PSVERSION = "3.0.0.0";

        public ConcurrentQueue<string> DebugMessages { get; private set; }

        IAzureEventListener _azureEventListener;
        protected static ConcurrentQueue<string> InitializationWarnings { get; set; } = new ConcurrentQueue<string>();

        private RecordingTracingInterceptor _httpTracingInterceptor;
        private object lockObject = new object();
        private AzurePSDataCollectionProfile _cachedProfile = null;

        protected IList<Regex> _matchers { get;  private set; }  = new List<Regex>();
        private static readonly Regex _defaultMatcher = new Regex("(\\s*\"access_token\"\\s*:\\s*)\"[^\"]+\"");

        // Using Ansi Code to control font color(97(Bold White)) and background color(0;120;212(RGB))
        private static readonly string ansiCodePrefix = "\u001b[97;48;2;0;120;212m";

        // using '[k' for erase in line. '[0m' to ending ansi code  
        private static readonly string ansiCodeSuffix = "\u001b[K\u001b[0m";

        protected AzurePSDataCollectionProfile _dataCollectionProfile
        {
            get
            {
                lock (lockObject)
                {
                    DataCollectionController controller;
                    if (_cachedProfile == null && AzureSession.Instance.TryGetComponent(DataCollectionController.RegistryKey, out controller))
                    {
                        _cachedProfile = controller.GetProfile(() => WriteWarning(DataCollectionWarning));
                    }
                    else if (_cachedProfile == null)
                    {
                        _cachedProfile = new AzurePSDataCollectionProfile();
                        WriteWarning(DataCollectionWarning);
                    }

                    return _cachedProfile;
                }
            }

            set
            {
                lock (lockObject)
                {
                    _cachedProfile = value;
                }
            }
        }

        protected static string _errorRecordFolderPath = null;
        protected static string _sessionId = Guid.NewGuid().ToString();
        protected const string _fileTimeStampSuffixFormat = "yyyy-MM-dd-THH-mm-ss-fff";
        protected string _clientRequestId = Guid.NewGuid().ToString();
        protected static DateTimeOffset? _previousEndTime = null;
        protected MetricHelper _metricHelper;
        protected AzurePSQoSEvent _qosEvent;
        protected DebugStreamTraceListener _adalListener;

        protected virtual bool IsUsageMetricEnabled
        {
            get { return true; }
        }

        protected virtual bool IsErrorMetricEnabled
        {
            get { return true; }
        }

        [Obsolete("Should use AzurePSCmdlet.PowerShellVersion")]
        protected string PSVersion
        {
            get
            {
                return LoadPowerShellVersion();
            }
        }

        /// <summary>
        /// Gets the PowerShell module name used for user agent header and telemetry.
        /// </summary>
        protected virtual string ModuleName { get; set; }

        /// <summary>
        /// Gets PowerShell module version used for user agent header and telemetry.
        /// </summary>
        protected string ModuleVersion { get; set; }

        /// <summary>
        /// The context for management cmdlet requests - includes account, tenant, subscription,
        /// and credential information for targeting and authorizing management calls.
        /// </summary>
        protected abstract IAzureContext DefaultContext { get; }

        protected abstract string DataCollectionWarning { get; }

        private SessionState _sessionState;

        public new SessionState SessionState
        {
            get
            {
                return _sessionState;
            }
            set
            {
                _sessionState = value;
            }
        }

        private RuntimeDefinedParameterDictionary _asJobDynamicParameters;

        public RuntimeDefinedParameterDictionary AsJobDynamicParameters
        {
            get
            {
                if (_asJobDynamicParameters == null)
                {
                    _asJobDynamicParameters = new RuntimeDefinedParameterDictionary();
                }
                return _asJobDynamicParameters;
            }
            set
            {
                _asJobDynamicParameters = value;
            }
        }

        /// <summary>
        /// Resolve user submitted paths correctly on all platforms
        /// </summary>
        /// <param name="path">Absolute or relative path</param>
        /// <returns>Absolute path</returns>
        public string ResolveUserPath(string path)
        {
            if (path == null)
            {
                return null;
            }

            if (SessionState == null)
            {
                return path;
            }

            try
            {
                return GetUnresolvedProviderPathFromPSPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Correctly join sections of a path and resolve final path correctly on all platforms
        /// </summary>
        /// <param name="paths">Sections of an absolute or relative path</param>
        /// <returns>Combined absolute path</returns>
        public string ResolveUserPath(string[] paths)
        {
            if (paths == null || paths.Count() == 0)
            {
                return "";
            }
            string path = paths[0];
            if (paths.Count() > 1)
            {
                for (int i = 1; i < paths.Count(); i++)
                {
                    path = Path.Combine(path, paths[i]);
                }
            }
            return ResolveUserPath(path);
        }

        /// <summary>
        /// Initializes AzurePSCmdlet properties.
        /// </summary>
        public AzurePSCmdlet()
        {
            DebugMessages = new ConcurrentQueue<string>();
        }

        // Register Dynamic Parameters for use in long running jobs
        public void RegisterDynamicParameters(RuntimeDefinedParameterDictionary parameters)
        {
            this.AsJobDynamicParameters = parameters;
        }


        /// <summary>
        /// Check whether the data collection is opted in from user
        /// </summary>
        /// <returns>true if allowed</returns>
        public bool IsDataCollectionAllowed()
        {
            if (_dataCollectionProfile != null &&
                _dataCollectionProfile.EnableAzureDataCollection.HasValue &&
                _dataCollectionProfile.EnableAzureDataCollection.Value)
            {
                return true;
            }

            return false;
        }

        protected virtual void LogCmdletStartInvocationInfo()
        {
            if (string.IsNullOrEmpty(ParameterSetName))
            {
                WriteDebugWithTimestamp(string.Format("{0} begin processing " +
                   "without ParameterSet.", this.GetType().Name));
            }
            else
            {
                WriteDebugWithTimestamp(string.Format("{0} begin processing " +
                   "with ParameterSet '{1}'.", this.GetType().Name, ParameterSetName));
            }
        }

        protected virtual void LogCmdletEndInvocationInfo()
        {
            string message = string.Format("{0} end processing.", this.GetType().Name);
            WriteDebugWithTimestamp(message);
        }

        protected void AddDebuggingFilter(Regex matcher)
        {
            _matchers.Add(matcher);
        }

        //Override this method in cmdlet if customized regedx filters needed for debugging message
        protected virtual void InitDebuggingFilter()
        {
            AddDebuggingFilter(_defaultMatcher);
        }

        protected virtual void SetupDebuggingTraces()
        {
            _httpTracingInterceptor = _httpTracingInterceptor ?? new
                RecordingTracingInterceptor(DebugMessages, _matchers);
            _adalListener = _adalListener ?? new DebugStreamTraceListener(DebugMessages);
            RecordingTracingInterceptor.AddToContext(_httpTracingInterceptor);
            DebugStreamTraceListener.AddAdalTracing(_adalListener);

            if (AzureSession.Instance.TryGetComponent(nameof(IAzureEventListenerFactory), out IAzureEventListenerFactory factory))
            {
                _azureEventListener = factory.GetAzureEventListener(
                    (message) =>
                    {
                        DebugMessages.Enqueue(message);
                    });
            }
        }

        protected virtual void TearDownDebuggingTraces()
        {
            RecordingTracingInterceptor.RemoveFromContext(_httpTracingInterceptor);
            DebugStreamTraceListener.RemoveAdalTracing(_adalListener);
            _azureEventListener?.Dispose();
            _azureEventListener = null;
            FlushDebugMessages();
        }


        protected virtual void SetupHttpClientPipeline()
        {
            AzureSession.Instance.ClientFactory.AddUserAgent("AzurePowershell", string.Format("v{0}", AzVersion));
            AzureSession.Instance.ClientFactory.AddUserAgent(PSVERSION, string.Format("v{0}", PowerShellVersion));
            AzureSession.Instance.ClientFactory.AddUserAgent(ModuleName, this.ModuleVersion);

            AzureSession.Instance.ClientFactory.AddHandler(
                new CmdletInfoHandler(this.CommandRuntime.ToString(),
                    this.ParameterSetName, this._clientRequestId));

        }

        protected virtual void TearDownHttpClientPipeline()
        {
            AzureSession.Instance.ClientFactory.RemoveUserAgent(ModuleName);
            AzureSession.Instance.ClientFactory.RemoveHandler(typeof(CmdletInfoHandler));
        }
        /// <summary>
        /// Cmdlet begin process. Write to logs, setup Http Tracing and initialize profile
        /// </summary>
        protected override void BeginProcessing()
        {
            FlushInitializationWarnings();
            SessionState = base.SessionState;
            var profile = _dataCollectionProfile;
            //TODO: Inject from CI server
            if(_metricHelper == null)
            {
                lock (lockObject)
                {
                    if (_metricHelper == null)
                    {
                        _metricHelper = new MetricHelper(profile);
                        _metricHelper.AddDefaultTelemetryClient();
                    }
                }
            }

            // Fetch module name and version which will be used by telemetry and useragent
            if (this.MyInvocation != null && this.MyInvocation.MyCommand != null)
            {
                this.ModuleName = this.MyInvocation.MyCommand.ModuleName;
                if (this.MyInvocation.MyCommand.Version != null)
                {
                    this.ModuleVersion = this.MyInvocation.MyCommand.Version.ToString();
                }
            }
            else
            {
                this.ModuleName = this.GetType().Assembly.GetName().Name;
                this.ModuleVersion = this.GetType().Assembly.GetName().Version.ToString();
            }

            InitializeQosEvent();
            LogCmdletStartInvocationInfo();
            InitDebuggingFilter();
            SetupDebuggingTraces();
            SetupHttpClientPipeline();
            base.BeginProcessing();

            //Now see if the cmdlet has any Breaking change attributes on it and process them if it does
            //This will print any breaking change attribute messages that are applied to the cmdlet
            WriteBreakingChangeOrPreviewMessage();
        }

        private void WriteBreakingChangeOrPreviewMessage()
        {
            if (AzureSession.Instance.TryGetComponent<IConfigManager>(nameof(IConfigManager), out var configManager)
                && configManager.GetConfigValue<bool>(ConfigKeysForCommon.DisplayBreakingChangeWarning))
            {
                BreakingChangeAttributeHelper.ProcessCustomAttributesAtRuntime(this.GetType(), this.MyInvocation, WriteWarning);
                PreviewAttributeHelper.ProcessCustomAttributesAtRuntime(this.GetType(), this.MyInvocation, WriteDebug);
            }
        }

        /// <summary>
        /// Perform end of pipeline processing.
        /// </summary>
        protected override void EndProcessing()
        {
            if (MetricHelper.IsCalledByUser() 
                && SurveyHelper.GetInstance().ShouldPromptAzSurvey() 
                && (AzureSession.Instance.TryGetComponent<IConfigManager>(nameof(IConfigManager), out var configManager)
                && !configManager.GetConfigValue<bool>(ConfigKeysForCommon.EnableInterceptSurvey).Equals(false)))
            {
                WriteSurvey();
                if (_qosEvent != null)
                {
                    _qosEvent.SurveyPrompted = true;
                }
            }
            if (MetricHelper.IsCalledByUser())
            {
                // Send telemetry when cmdlet is directly called by user
                LogQosEvent();
            }
            else {
                // When cmdlet is called within another cmdlet, we will not add a new telemetry, but add the cmdlet name to InternalCalledCmdlets
                MetricHelper.AppendInternalCalledCmdlet(this.MyInvocation?.MyCommand?.Name);
            }
            LogCmdletEndInvocationInfo();
            TearDownDebuggingTraces();
            TearDownHttpClientPipeline();
            _previousEndTime = DateTimeOffset.Now;
            base.EndProcessing();
        }


        protected string CurrentPath()
        {
            // SessionState is only available within PowerShell so default to
            // the TestMockSupport.TestExecutionFolder when being run from tests.
            return (SessionState != null) ?
                SessionState.Path.CurrentLocation.Path :
                TestMockSupport.TestExecutionFolder;
        }

        protected bool IsVerbose()
        {
            bool verbose = MyInvocation.BoundParameters.ContainsKey("Verbose")
                && ((SwitchParameter)MyInvocation.BoundParameters["Verbose"]).ToBool();
            return verbose;
        }

        protected void WriteSurvey()
        {
            HostInformationMessage newLine = new HostInformationMessage()
            {
                Message = ansiCodePrefix + "\n" + ansiCodeSuffix
            };
            HostInformationMessage howWas = new HostInformationMessage()
            {
                Message = ansiCodePrefix + "[Survey] Help us improve Azure PowerShell by sharing your experience. This survey should take about 5 minutes. Run "+ ansiCodeSuffix,
                NoNewLine = true
            };
            HostInformationMessage link = new HostInformationMessage()
            {
                Message = ansiCodePrefix + "'Open-AzSurveyLink'"+ ansiCodeSuffix,
                NoNewLine = true,
            };
            HostInformationMessage action = new HostInformationMessage()
            {
                Message = ansiCodePrefix + " to open in browser. Learn more at "+ ansiCodeSuffix,
                NoNewLine = true,

            };
            HostInformationMessage website = new HostInformationMessage()
            {
                Message = ansiCodePrefix + "https://go.microsoft.com/fwlink/?linkid=2202892"+ ansiCodeSuffix,
                NoNewLine = true,
            };
            HostInformationMessage dot = new HostInformationMessage()
            {
                Message = ansiCodePrefix + "."+ ansiCodeSuffix,
                NoNewLine = true,
            };
            WriteInformation(newLine, new string[] { "PSHOST" });
            WriteInformation(howWas, new string[] { "PSHOST" });
            WriteInformation(link, new string[] { "PSHOST" });
            WriteInformation(action, new string[] { "PSHOST" });
            WriteInformation(website, new string[] { "PSHOST" });
            WriteInformation(dot, new string[] { "PSHOST" });
            WriteInformation(newLine, new string[] { "PSHOST" });


        }
        protected new void WriteError(ErrorRecord errorRecord)
        {
            FlushDebugMessages(IsDataCollectionAllowed());
            if (_qosEvent != null && errorRecord != null)
            {
                _qosEvent.Exception = errorRecord.Exception;
                _qosEvent.IsSuccess = false;
            }
            base.WriteError(errorRecord);
            if (AzureSession.Instance.TryGetComponent<IConfigManager>(nameof(IConfigManager), out var configManager)
                && configManager.GetConfigValue<bool>(ConfigKeysForCommon.DisplayBreakingChangeWarning))
            {
                PreviewAttributeHelper.ProcessCustomAttributesAtRuntime(this.GetType(), this.MyInvocation, WriteWarning);
            }
        }

        protected new void ThrowTerminatingError(ErrorRecord errorRecord)
        {
            FlushDebugMessages(IsDataCollectionAllowed());
            base.ThrowTerminatingError(errorRecord);
        }

        protected new void WriteObject(object sendToPipeline)
        {
            FlushDebugMessages();
            base.WriteObject(sendToPipeline);
        }

        protected new void WriteObject(object sendToPipeline, bool enumerateCollection)
        {
            FlushDebugMessages();
            base.WriteObject(sendToPipeline, enumerateCollection);
        }

        protected new void WriteVerbose(string text)
        {
            FlushDebugMessages();
            base.WriteVerbose(text);
        }

        protected new void WriteWarning(string text)
        {
            FlushDebugMessages();
            base.WriteWarning(text);
        }

        protected new void WriteCommandDetail(string text)
        {
            FlushDebugMessages();
            base.WriteCommandDetail(text);
        }

        protected new void WriteProgress(ProgressRecord progressRecord)
        {
            FlushDebugMessages();
            base.WriteProgress(progressRecord);
        }

        protected new void WriteDebug(string text)
        {
            FlushDebugMessages();
            base.WriteDebug(text);
        }

        protected void WriteVerboseWithTimestamp(string message, params object[] args)
        {
            if (CommandRuntime != null)
            {
                WriteVerbose(string.Format("{0:T} - {1}", DateTime.Now, string.Format(message, args)));
            }
        }

        protected void WriteVerboseWithTimestamp(string message)
        {
            if (CommandRuntime != null)
            {
                WriteVerbose(string.Format("{0:T} - {1}", DateTime.Now, message));
            }
        }

        protected void WriteWarningWithTimestamp(string message)
        {
            if (CommandRuntime != null)
            {
                WriteWarning(string.Format("{0:T} - {1}", DateTime.Now, message));
            }
        }

        protected void WriteDebugWithTimestamp(string message, params object[] args)
        {
            if (CommandRuntime != null)
            {
                WriteDebug(string.Format("{0:T} - {1}", DateTime.Now, string.Format(message, args)));
            }
        }

        protected void WriteDebugWithTimestamp(string message)
        {
            if (CommandRuntime != null)
            {
                WriteDebug(string.Format("{0:T} - {1}", DateTime.Now, message));
            }
        }

        protected void WriteErrorWithTimestamp(string message)
        {
            if (CommandRuntime != null)
            {
                WriteError(
                new ErrorRecord(new Exception(string.Format("{0:T} - {1}", DateTime.Now, message)),
                string.Empty,
                ErrorCategory.NotSpecified,
                null));
            }
        }

        /// <summary>
        /// Write an error message for a given exception.
        /// </summary>
        /// <param name="ex">The exception resulting from the error.</param>
        protected virtual void WriteExceptionError(Exception ex)
        {
            Debug.Assert(ex != null, "ex cannot be null or empty.");
            WriteError(new ErrorRecord(ex, string.Empty, ErrorCategory.CloseError, null));
        }

        protected PSObject ConstructPSObject(string typeName, params object[] args)
        {
            return PowerShellUtilities.ConstructPSObject(typeName, args);
        }

        protected void SafeWriteOutputPSObject(string typeName, params object[] args)
        {
            PSObject customObject = this.ConstructPSObject(typeName, args);
            WriteObject(customObject);
        }

        private void FlushDebugMessages(bool record = false)
        {
            if (record)
            {
                RecordDebugMessages();
            }

            string message;
            while (DebugMessages.TryDequeue(out message))
            {
                base.WriteDebug(message);
            }
        }

        public void WriteInitializationWarnings(string message)
        {
            InitializationWarnings.Enqueue(message);
        }

        protected void FlushInitializationWarnings()
        {
            string message;
            while (InitializationWarnings.TryDequeue(out message))
            {
                base.WriteWarning(message);
            }
        }

        protected virtual void InitializeQosEvent()
        {
            _qosEvent = new AzurePSQoSEvent()
            {
                ClientRequestId = this._clientRequestId,
                // Use SessionId from MetricHelper so that generated cmdlet and handcrafted cmdlet could share the same Id
                SessionId = MetricHelper.SessionId,
                IsSuccess = true,
                ParameterSetName = this.ParameterSetName,
                PreviousEndTime = _previousEndTime
            };

            if (AzVersion == null)
            {
                AzVersion = this.LoadModuleVersion("Az", true);
                UserAgent = new ProductInfoHeaderValue("AzurePowershell", string.Format("Az{0}", AzVersion)).ToString();
                string hostEnv = Environment.GetEnvironmentVariable("AZUREPS_HOST_ENVIRONMENT");
                if (!String.IsNullOrWhiteSpace(hostEnv))
                    UserAgent += string.Format(" {0}", hostEnv.Trim());
                PowerShellVersion = this.LoadPowerShellVersion();
                PSHostName = this.Host?.Name;
                PSHostVersion = this.Host?.Version?.ToString();
            }
            if (AzAccountsVersion == null)
            {
                AzAccountsVersion = this.LoadModuleVersion("Az.Accounts", false);
            }

            _qosEvent.AzVersion = AzVersion;
            _qosEvent.AzAccountsVersion = AzAccountsVersion;
            _qosEvent.UserAgent = UserAgent;
            _qosEvent.PSVersion = PowerShellVersion;
            _qosEvent.HostVersion = PSHostVersion;
            _qosEvent.PSHostName = PSHostName;
            _qosEvent.ModuleName = this.ModuleName;
            _qosEvent.ModuleVersion = this.ModuleVersion;
            if (this.MyInvocation != null && this.MyInvocation.MyCommand != null)
            {
                _qosEvent.CommandName = this.MyInvocation.MyCommand.Name;
            }
            else
            {
                _qosEvent.CommandName = this.GetType().Name;
            }

            if (this.MyInvocation != null && !string.IsNullOrWhiteSpace(this.MyInvocation.InvocationName))
            {
                _qosEvent.InvocationName = this.MyInvocation.InvocationName;
            }

            if (this.MyInvocation != null && this.MyInvocation.BoundParameters != null
                && this.MyInvocation.BoundParameters.Keys != null)
            {
                _qosEvent.Parameters = string.Join(" ",
                    this.MyInvocation.BoundParameters.Keys.Select(
                        s => string.Format(CultureInfo.InvariantCulture, "-{0} ***", s)));
            }
        }

        private void RecordDebugMessages()
        {
            try
            {
                // Create 'ErrorRecords' folder under profile directory, if not exists
                if (string.IsNullOrEmpty(_errorRecordFolderPath)
                    || !Directory.Exists(_errorRecordFolderPath))
                {
                    _errorRecordFolderPath = Path.Combine(AzurePowerShell.ProfileDirectory,
                        "ErrorRecords");
                    Directory.CreateDirectory(_errorRecordFolderPath);
                }

                CommandInfo cmd = this.MyInvocation.MyCommand;

                string filePrefix = cmd.Name;
                string timeSampSuffix = DateTime.Now.ToString(_fileTimeStampSuffixFormat);
                string fileName = filePrefix + "_" + timeSampSuffix + ".log";
                string filePath = Path.Combine(_errorRecordFolderPath, fileName);

                StringBuilder sb = new StringBuilder();
                sb.Append("Module : ").AppendLine(cmd.ModuleName);
                sb.Append("Cmdlet : ").AppendLine(cmd.Name);

                sb.AppendLine("Parameters");
                foreach (var item in this.MyInvocation.BoundParameters)
                {
                    sb.Append(" -").Append(item.Key).Append(" : ");
                    sb.AppendLine(item.Value == null ? "null" : item.Value.ToString());
                }

                sb.AppendLine();

                foreach (var content in DebugMessages)
                {
                    sb.AppendLine(content);
                }

                AzureSession.Instance.DataStore.WriteFile(filePath, sb.ToString());
            }
            catch
            {
                // do not throw an error if recording debug messages fails
            }
        }

        /// <summary>
        /// Invoke this method when the cmdlet is completed or terminated.
        /// </summary>
        protected void LogQosEvent()
        {
            if (_qosEvent == null)
            {
                return;
            }

            _qosEvent.FinishQosEvent();

            if (!IsUsageMetricEnabled && (!IsErrorMetricEnabled || _qosEvent.IsSuccess))
            {
                return;
            }

            if (!IsDataCollectionAllowed())
            {
                return;
            }

            WriteDebug(_qosEvent.ToString());

            try
            {
                _metricHelper.LogQoSEvent(_qosEvent, IsUsageMetricEnabled, IsErrorMetricEnabled);
                _metricHelper.FlushMetric();
            }
            catch (Exception e)
            {
                //Swallow error from Application Insights event collection.
                WriteWarning(e.ToString());
            }
        }

        /// <summary>
        /// Guards execution of the given action using ShouldProcess and ShouldContinue.  This is a legacy
        /// version forcompatibility with older RDFE cmdlets.
        /// </summary>
        /// <param name="force">Do not ask for confirmation</param>
        /// <param name="continueMessage">Message to describe the action</param>
        /// <param name="processMessage">Message to prompt after the active is performed.</param>
        /// <param name="target">The target name.</param>
        /// <param name="action">The action code</param>
        protected virtual void ConfirmAction(bool force, string continueMessage, string processMessage, string target,
            Action action)
        {
            if (_qosEvent != null)
            {
                _qosEvent.PauseQoSTimer();
            }

            if (force || ShouldContinue(continueMessage, ""))
            {
                if (ShouldProcess(target, processMessage))
                {
                    if (_qosEvent != null)
                    {
                        _qosEvent.ResumeQosTimer();
                    }
                    action();
                }
            }
        }

        /// <summary>
        /// Guards execution of the given action using ShouldProcess and ShouldContinue.  The optional
        /// useSHouldContinue predicate determines whether SHouldContinue should be called for this
        /// particular action (e.g. a resource is being overwritten). By default, both
        /// ShouldProcess and ShouldContinue will be executed.  Cmdlets that use this method overload
        /// must have a force parameter.
        /// </summary>
        /// <param name="force">Do not ask for confirmation</param>
        /// <param name="continueMessage">Message to describe the action</param>
        /// <param name="processMessage">Message to prompt after the active is performed.</param>
        /// <param name="target">The target name.</param>
        /// <param name="action">The action code</param>
        /// <param name="useShouldContinue">A predicate indicating whether ShouldContinue should be invoked for thsi action</param>
        protected virtual void ConfirmAction(bool force, string continueMessage, string processMessage, string target, Action action, Func<bool> useShouldContinue)
        {
            if (null == useShouldContinue)
            {
                useShouldContinue = () => true;
            }
            if (_qosEvent != null)
            {
                _qosEvent.PauseQoSTimer();
            }

            if (ShouldProcess(target, processMessage))
            {
                if (force || !useShouldContinue() || ShouldContinue(continueMessage, ""))
                {
                    if (_qosEvent != null)
                    {
                        _qosEvent.ResumeQosTimer();
                    }
                    action();
                }
            }
        }

        /// <summary>
        /// Prompt for confirmation depending on the ConfirmLevel. By default No confirmation prompt
        /// occurs unless ConfirmLevel >= $ConfirmPreference.  Guarding the actions of a cmdlet with this
        /// method will enable the cmdlet to support -WhatIf and -Confirm parameters.
        /// </summary>
        /// <param name="processMessage">The change being made to the resource</param>
        /// <param name="target">The resource that is being changed</param>
        /// <param name="action">The action to perform if confirmed</param>
        protected virtual void ConfirmAction(string processMessage, string target, Action action)
        {
            if (_qosEvent != null)
            {
                _qosEvent.PauseQoSTimer();
            }

            if (ShouldProcess(target, processMessage))
            {
                if (_qosEvent != null)
                {
                    _qosEvent.ResumeQosTimer();
                }
                action();
            }
        }

        public virtual void ExecuteCmdlet()
        {
            // Do nothing.
        }

        protected override void ProcessRecord()
        {
            try
            {
                base.ProcessRecord();
                this.ExecuteSynchronouslyOrAsJob();
            }
            catch (Exception ex) when (!IsTerminatingError(ex))
            {
                WriteExceptionError(ex);
            }
        }

        private string _implementationBackgroundJobDescription;

        /// <summary>
        /// Job Name paroperty iof this cmdlet is run as a job
        /// </summary>
        public virtual string ImplementationBackgroundJobDescription
        {
            get
            {
                if (_implementationBackgroundJobDescription != null)
                {
                    return _implementationBackgroundJobDescription;
                }
                else
                {
                    string name = "Long Running Azure Operation";
                    string commandName = MyInvocation?.MyCommand?.Name;
                    string objectName = null;
                    if (this.IsBound("Name"))
                    {
                        objectName = MyInvocation.BoundParameters["Name"].ToString();
                    }
                    else if (this.IsBound("InputObject") == true)
                    {
                        var type = MyInvocation.BoundParameters["InputObject"].GetType();
                        var inputObject = Convert.ChangeType(MyInvocation.BoundParameters["InputObject"], type);
                        if (type.GetProperty("Name") != null)
                        {
                            objectName = inputObject.GetType().GetProperty("Name").GetValue(inputObject).ToString();
                        }
                        else if (type.GetProperty("ResourceId") != null)
                        {
                            string[] tokens = inputObject.GetType().GetProperty("ResourceId").GetValue(inputObject).ToString().Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (tokens.Length >= 8)
                            {
                                objectName = tokens[tokens.Length - 1];
                            }
                        }
                    }
                    else if (this.IsBound("ResourceId") == true)
                    {
                        string[] tokens = MyInvocation.BoundParameters["ResourceId"].ToString().Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 8)
                        {
                            objectName = tokens[tokens.Length - 1];
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(commandName))
                    {
                        if (!string.IsNullOrWhiteSpace(objectName))
                        {
                            name = string.Format("Long Running Operation for '{0}' on resource '{1}'", commandName, objectName);
                        }
                        else
                        {
                            name = string.Format("Long Running Operation for '{0}'", commandName);
                        }
                    }

                    return name;
                }
            }
            set
            {
                _implementationBackgroundJobDescription = value;
            }
        }

        public void SetBackgroundJobDescription(string jobName)
        {
            ImplementationBackgroundJobDescription = jobName;
        }

        protected virtual void Dispose(bool disposing)
        {
            try
            {
                FlushDebugMessages();
            }
            catch { }
            if (disposing && _adalListener != null)
            {
                _adalListener.Dispose();
                _adalListener = null;
            }

        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual bool IsTerminatingError(Exception ex)
        {
            var pipelineStoppedEx = ex as PipelineStoppedException;
            if (pipelineStoppedEx != null && pipelineStoppedEx.InnerException == null)
            {
                return true;
            }

            return false;
        }
        //The latest version of Az Wrapper in local. It will be loaded in runtime when the first cmdlet is executed.
        //If there is no Az module, the version is "0.0.0"
        public static string AzVersion { set; get; }

        public static string AzAccountsVersion { set; get; }


        //Initialized once AzVersion is loaded.
        //Format: AzurePowershell/Az0.0.0 %AZUREPS_HOST_ENVIROMENT%
        public static string UserAgent { set; get; }
        public static string PowerShellVersion { set; get; }
        public static string PSHostVersion { set; get; }
        public static string PSHostName { set; get; }

        protected string LoadModuleVersion(String Module, bool ListAvailable)
        {
            Version defaultVersion = new Version("0.0.0");
            if (this.Host == null)
            {
                WriteDebug($"Cannot fetch {Module} version due to no host in current environment");
                return defaultVersion.ToString();
            }

            Version latestAz = defaultVersion;
            string latestSuffix = "";

            try
            {
                List<PSObject> outputs;
                if (ListAvailable)
                {
                   outputs = this.ExecuteScript<PSObject>($"Get-Module -Name {Module} -ListAvailable");
                }
                else
                {
                   outputs = this.ExecuteScript<PSObject>($"Get-Module -Name {Module}");
                }
                foreach (PSObject obj in outputs)
                {
                    string psVersion = obj.Properties["Version"].Value.ToString();
                    int pos = psVersion.IndexOf('-');
                    string currentSuffix = (pos == -1 || pos == psVersion.Length - 1) ? "" : psVersion.Substring(pos + 1);
                    Version currentAz = (pos == -1) ? new Version(psVersion) : new Version(psVersion.Substring(0, pos));
                    if (currentAz > latestAz)
                    {
                        latestAz = currentAz;
                        latestSuffix = currentSuffix;
                    }
                    else if (currentAz == latestAz)
                    {
                        latestSuffix = String.Compare(latestSuffix, currentSuffix) > 0 ? latestSuffix : currentSuffix;
                    }
                }
            }
            catch (Exception e)
            {
                WriteDebug(string.Format($"Cannot fetch {Module} version due to exception: {0}", e.Message));
                return defaultVersion.ToString();
            }

            string ret = latestAz.ToString();
            if (!String.IsNullOrEmpty(latestSuffix))
            {
                ret += "-" + latestSuffix;
            }
            WriteDebug(string.Format($"Got version {0} of {Module}", ret));
            return ret;
        }

        private string LoadPowerShellVersion()
        {
            try
            {
                var outputs = this.ExecuteScript<PSObject>("$Host.Runspace.Version");
                foreach (PSObject obj in outputs)
                {
                    string psVersion = obj.ToString();
                    return string.IsNullOrWhiteSpace(psVersion) ? DEFAULT_PSVERSION: psVersion;
                }
            }
            catch (Exception e)
            {
                WriteDebug(string.Format("Cannot fetch PowerShell version due to exception: {0}", e.Message));
            }
            return DEFAULT_PSVERSION;
        }
    }
}
