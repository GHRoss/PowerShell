/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Runspaces.Internal;
using System.Management.Automation.Remoting.Client;
using System.Threading;
using Dbg = System.Management.Automation.Diagnostics;


namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// This cmdlet establishes a new Runspace either on the local machine or
    /// on the specified remote machine(s). The runspace established can be used 
    /// to invoke expressions remotely.
    ///
    /// The cmdlet can be used in the following ways:
    /// 
    /// Open a local runspace
    /// $rs = New-PSSession
    ///
    /// Open a runspace to a remote system.
    /// $rs = New-PSSession -Machine PowerShellWorld
    ///
    /// Create a runspace specifying that it is globally scoped.
    /// $global:rs = New-PSSession -Machine PowerShellWorld
    ///
    /// Create a collection of runspaces
    /// $runspaces = New-PSSession -Machine PowerShellWorld,PowerShellPublish,PowerShellRepo
    /// 
    /// Create a set of Runspaces using the Secure Socket Layer by specifying the URI form.  
    /// This assumes that an shell by the name of E12 exists on the remote server.  
    ///     $serverURIs = 1..8 | %{ "SSL://server${_}:443/E12" }
    ///     $rs = New-PSSession -URI $serverURIs
    /// 
    /// Create a runspace by connecting to port 8081 on servers s1, s2 and s3
    /// $rs = New-PSSession -computername s1,s2,s3 -port 8081
    /// 
    /// Create a runspace by connecting to port 443 using ssl on servers s1, s2 and s3
    /// $rs = New-PSSession -computername s1,s2,s3 -port 443 -useSSL
    /// 
    /// Create a runspace by connecting to port 8081 on server s1 and run shell named E12.
    /// This assumes that a shell by the name E12 exists on the remote server
    /// $rs = New-PSSession -computername s1 -port 8061 -ShellName E12
    /// </summary>
    /// 
    [Cmdlet(VerbsCommon.New, "PSSession", DefaultParameterSetName = "ComputerName",
        HelpUri = "http://go.microsoft.com/fwlink/?LinkID=135237", RemotingCapability = RemotingCapability.OwnedByCommand)]
    [OutputType(typeof(PSSession))]
    public class NewPSSessionCommand : PSRemotingBaseCmdlet, IDisposable
    {
        #region Parameters

        /// <summary>
        /// This parameter represents the address(es) of the remote
        /// computer(s). The following formats are supported:
        ///      (a) Computer name 
        ///      (b) IPv4 address : 132.3.4.5
        ///      (c) IPv6 address: 3ffe:8311:ffff:f70f:0:5efe:172.30.162.18
        /// 
        /// </summary>
        [Parameter(Position = 0,
                   ValueFromPipeline = true,
                   ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.ComputerNameParameterSet)]
        [Alias("Cn")]
        [ValidateNotNullOrEmpty]
        public override String[] ComputerName
        {
            get
            {
                return computerNames;
            }
            set
            {
                computerNames = value;
            }
        }
        private String[] computerNames;

        /// <summary>
        /// Specifies the credentials of the user to impersonate in the 
        /// remote machine. If this parameter is not specified then the 
        /// credentials of the current user process will be assumed.
        /// </summary>     
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = PSRemotingBaseCmdlet.ComputerNameParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = PSRemotingBaseCmdlet.UriParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = PSRemotingBaseCmdlet.VMIdParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = PSRemotingBaseCmdlet.VMNameParameterSet)]
        [Credential()]
        public override PSCredential Credential
        {
            get { return base.Credential; }
            set
            {
                base.Credential = value;
            }
        }

        /// <summary>
        /// The PSSession object describing the remote runspace
        /// using which the specified cmdlet operation will be performed
        /// </summary>
        [Parameter(Position = 0,
                   ValueFromPipelineByPropertyName = true,
                   ValueFromPipeline = true,
                   ParameterSetName = NewPSSessionCommand.SessionParameterSet)]
        [ValidateNotNullOrEmpty]
        public override PSSession[] Session
        {
            get
            {
                return remoteRunspaceInfos;
            }
            set
            {
                remoteRunspaceInfos = value;
            }
        }
        private PSSession[] remoteRunspaceInfos;

        /// <summary>
        /// Friendly names for the new PSSessions
        /// </summary>
        [Parameter()]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public String[] Name
        {
            get
            {
                return names;
            }
            set
            {
                names = value;
            }
        }
        private String[] names;

        /// <summary>
        /// When set and in loopback scenario (localhost) this enables creation of WSMan
        /// host process with the user interactive token, allowing PowerShell script network access, 
        /// i.e., allows going off box.  When this property is true and a PSSession is disconnected, 
        /// reconnection is allowed only if reconnecting from a PowerShell session on the same box.
        /// </summary>
        [Parameter()]
        public SwitchParameter EnableNetworkAccess
        {
            get { return enableNetworkAccess; }
            set { enableNetworkAccess = value; }
        }
        private SwitchParameter enableNetworkAccess;

        /// <summary>
        /// For WSMan sessions:
        /// If this parameter is not specified then the value specified in
        /// the environment variable DEFAULTREMOTESHELLNAME will be used. If 
        /// this is not set as well, then Microsoft.PowerShell is used.
        ///
        /// For VM/Container sessions:
        /// If this parameter is not specified then no configuration is used.
        /// </summary>      
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.ComputerNameParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.UriParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.ContainerIdParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.ContainerNameParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.VMIdParameterSet)]
        [Parameter(ValueFromPipelineByPropertyName = true,
                   ParameterSetName = NewPSSessionCommand.VMNameParameterSet)]
        public String ConfigurationName
        {
            get
            {
                return shell;
            }
            set
            {
                shell = value;
            }
        }
        private String shell;

        #endregion Parameters

        #region Cmdlet Overrides

        /// <summary>
        /// The throttle limit will be set here as it needs to be done 
        /// only once per cmdlet and not for every call
        /// </summary>
        protected override void BeginProcessing()
        {
            base.BeginProcessing();
            operationsComplete.Reset();
            throttleManager.ThrottleLimit = ThrottleLimit;
            throttleManager.ThrottleComplete +=
                new EventHandler<EventArgs>(HandleThrottleComplete);

            if (String.IsNullOrEmpty(ConfigurationName))
            {
                if ((ParameterSetName == NewPSSessionCommand.ComputerNameParameterSet) ||
                    (ParameterSetName == NewPSSessionCommand.UriParameterSet))
                {
                    // set to default value for WSMan session
                    ConfigurationName = ResolveShell(null);
                }
                else
                {
                    // convert null to String.Empty for VM/Container session
                    ConfigurationName = String.Empty;
                }
            }
        } // BeginProcessing

        /// <summary>
        /// The runspace objects will be created using OpenAsync. 
        /// At the end, the method will check if any runspace
        /// opened has already become available. If so, then it 
        /// will be written to the pipeline
        /// </summary>
        protected override void ProcessRecord()
        {
            List<RemoteRunspace> remoteRunspaces = null;
            List<IThrottleOperation> operations = new List<IThrottleOperation>();

            switch (ParameterSetName)
            {
                case NewPSSessionCommand.SessionParameterSet:
                    {
                        remoteRunspaces = CreateRunspacesWhenRunspaceParameterSpecified();
                    }
                    break;

                case "Uri":
                    {
                        remoteRunspaces = CreateRunspacesWhenUriParameterSpecified();
                    }
                    break;

                case NewPSSessionCommand.ComputerNameParameterSet:
                    {
                        remoteRunspaces = CreateRunspacesWhenComputerNameParameterSpecified();
                    }
                    break;

                case NewPSSessionCommand.VMIdParameterSet:
                case NewPSSessionCommand.VMNameParameterSet:
                    {
                        remoteRunspaces = CreateRunspacesWhenVMParameterSpecified();
                    }
                    break;
                    
                case NewPSSessionCommand.ContainerIdParameterSet:
                case NewPSSessionCommand.ContainerNameParameterSet:                    
                    {
                        remoteRunspaces = CreateRunspacesWhenContainerParameterSpecified();
                    }
                    break;

                default:
                    {
                        Dbg.Assert(false, "Missing paramenter set in switch statement");
                        remoteRunspaces = new List<RemoteRunspace>(); // added to avoid prefast warning
                    }
                    break;
            } // switch (ParameterSetName...

            foreach (RemoteRunspace remoteRunspace in remoteRunspaces)
            {
                remoteRunspace.Events.ReceivedEvents.PSEventReceived += OnRunspacePSEventReceived;

                OpenRunspaceOperation operation = new OpenRunspaceOperation(remoteRunspace);
                // HandleRunspaceStateChanged callback is added before ThrottleManager complete
                // callback handlers so HandleRunspaceStateChanged will always be called first.
                operation.OperationComplete +=
                    new EventHandler<OperationStateEventArgs>(HandleRunspaceStateChanged);
                remoteRunspace.URIRedirectionReported += HandleURIDirectionReported;
                operations.Add(operation);
            }

            // submit list of operations to throttle manager to start opening
            // runspaces
            throttleManager.SubmitOperations(operations);

            // Add to list for clean up.
            this.allOperations.Add(operations);

            // If there are any runspaces opened asynchronously 
            // that are ready now, check their status and do
            // necessary action. If there are any error records
            // or verbose messages write them as well
            Collection<object> streamObjects =
                stream.ObjectReader.NonBlockingRead();

            foreach (object streamObject in streamObjects)
            {
                WriteStreamObject((Action<Cmdlet>)streamObject);
            } // foreach


        }// ProcessRecord()

        /// <summary>
        /// OpenAsync would have been called from ProcessRecord. This method
        /// will wait until all runspaces are opened and then write them to
        /// the pipeline as and when they become available.
        /// </summary>
        protected override void EndProcessing()
        {
            // signal to throttle manager end of submit operations
            throttleManager.EndSubmitOperations();

            while (true)
            {
                // Keep reading objects until end of pipeline is encountered
                stream.ObjectReader.WaitHandle.WaitOne();

                if (!stream.ObjectReader.EndOfPipeline)
                {
                    Object streamObject = stream.ObjectReader.Read();
                    WriteStreamObject((Action<Cmdlet>)streamObject);
                }
                else
                {
                    break;
                }
            } // while ...
        }// EndProcessing()

        /// <summary>
        /// This method is called when the user sends a stop signal to the 
        /// cmdlet. The cmdlet will not exit until it has completed
        /// creating all the runspaces (basically the runspaces its
        /// waiting on OpenAsync is made available). However, when a stop
        /// signal is sent, CloseAsyn needs to be called to close all the
        /// pending runspaces
        /// </summary>
        /// <remarks>This is called from a separate thread so need to worry 
        /// about concurrency issues
        /// </remarks>
        protected override void StopProcessing()
        {
            // close the outputStream so that futher writes to the outputStream
            // are not possible
            stream.ObjectWriter.Close();

            // for all the runspaces that have been submitted for opening
            // call StopOperation on each object and quit
            throttleManager.StopAllOperations();

        }// StopProcessing()

        #endregion Cmdlet Overrides

        #region IDisposable Overrides

        /// <summary>
        /// Dispose method of IDisposable. Gets called in the following cases:
        ///     1. Pipeline explicitly calls dispose on cmdlets
        ///     2. Called by the garbage collector 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        #endregion IDisposable Overrides

        #region Private Methods

        /// <summary>
        /// Adds forwarded events to the local queue
        /// </summary>
        private void OnRunspacePSEventReceived(object sender, PSEventArgs e)
        {
            if(this.Events != null)
                this.Events.AddForwardedEvent(e);
        }

        /// <summary>
        /// When the client remote session reports a URI redirection, this method will report the
        /// message to the user as a Warning using Host method calls.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="eventArgs"></param>
        private void HandleURIDirectionReported(object sender, RemoteDataEventArgs<Uri> eventArgs)
        {
            string message = StringUtil.Format(RemotingErrorIdStrings.URIRedirectWarningToHost, eventArgs.Data.OriginalString);
            Action<Cmdlet> warningWriter = delegate(Cmdlet cmdlet)
            {
                cmdlet.WriteWarning(message);
            };
            stream.Write(warningWriter);
        }

        /// <summary>
        /// Handles state changes for Runspace
        /// </summary>
        /// <param name="sender">Sender of this event</param>
        /// <param name="stateEventArgs">Event information object which describes
        /// the event which triggered this method</param>
        private void HandleRunspaceStateChanged(object sender, OperationStateEventArgs stateEventArgs)
        {
            if (sender == null)
            {
                throw PSTraceSource.NewArgumentNullException("sender");
            }

            if (stateEventArgs == null)
            {
                throw PSTraceSource.NewArgumentNullException("stateEventArgs");
            }

            RunspaceStateEventArgs runspaceStateEventArgs =
                        stateEventArgs.BaseEvent as RunspaceStateEventArgs;
            RunspaceStateInfo stateInfo = runspaceStateEventArgs.RunspaceStateInfo;
            RunspaceState state = stateInfo.State;
            OpenRunspaceOperation operation = sender as OpenRunspaceOperation;
            RemoteRunspace remoteRunspace = operation.OperatedRunspace;

            // since we got state changed event..we dont need to listen on
            // URI redirections anymore
            if (null != remoteRunspace)
            {
                remoteRunspace.URIRedirectionReported -= HandleURIDirectionReported;
            }

            PipelineWriter writer = stream.ObjectWriter;
            Exception reason = runspaceStateEventArgs.RunspaceStateInfo.Reason;

            switch (state)
            {
                case RunspaceState.Opened:
                    {
                        // Indicates that runspace is successfully opened
                        // Write it to PipelineWriter to be handled in 
                        // HandleRemoteRunspace
                        PSSession remoteRunspaceInfo = new PSSession(remoteRunspace);

                        this.RunspaceRepository.Add(remoteRunspaceInfo);

                        Action<Cmdlet> outputWriter = delegate(Cmdlet cmdlet)
                        {
                            cmdlet.WriteObject(remoteRunspaceInfo);
                        };
                        if (writer.IsOpen)
                        {
                            writer.Write(outputWriter);
                        }
                    }
                    break;

                case RunspaceState.Broken:
                    {
                        // Open resulted in a broken state. Extract reason
                        // and write an error record

                        // set the transport message in the error detail so that
                        // the user can directly get to see the message without
                        // having to mine through the error record details
                        PSRemotingTransportException transException =
                            reason as PSRemotingTransportException;
                        String errorDetails = null;
                        if (transException != null)
                        {
                            OpenRunspaceOperation senderAsOp = sender as OpenRunspaceOperation;

                            if (senderAsOp != null)
                            {
                                String host = senderAsOp.OperatedRunspace.ConnectionInfo.ComputerName;

                                if (transException.ErrorCode ==
                                    System.Management.Automation.Remoting.Client.WSManNativeApi.ERROR_WSMAN_REDIRECT_REQUESTED)
                                {
                                    // Handling a special case for redirection..we should talk about
                                    // AllowRedirection parameter and WSManMaxRedirectionCount preference
                                    // variables
                                    string message = PSRemotingErrorInvariants.FormatResourceString(
                                        RemotingErrorIdStrings.URIRedirectionReported,
                                        transException.Message,
                                        "MaximumConnectionRedirectionCount",
                                        Microsoft.PowerShell.Commands.PSRemotingBaseCmdlet.DEFAULT_SESSION_OPTION,
                                        "AllowRedirection");

                                    errorDetails = "[" + host + "] " + message;
                                        
                                }
                                else
                                {
                                    errorDetails = "[" + host + "] ";
                                    if (!String.IsNullOrEmpty(transException.Message))
                                    {
                                        errorDetails += transException.Message;
                                    }
                                    else if (!String.IsNullOrEmpty(transException.TransportMessage))
                                    {
                                        errorDetails += transException.TransportMessage;
                                    }
                                }
                            }

                        }

                        // add host identification information in data structure handler message
                        PSRemotingDataStructureException protoExeption = reason as PSRemotingDataStructureException;

                        if (protoExeption != null)
                        {
                            OpenRunspaceOperation senderAsOp = sender as OpenRunspaceOperation;

                            if (senderAsOp != null)
                            {
                                String host = senderAsOp.OperatedRunspace.ConnectionInfo.ComputerName;

                                errorDetails = "[" + host + "] " + protoExeption.Message;
                            }
                        }

                        if (reason == null)
                        {
                            reason = new RuntimeException(this.GetMessage(RemotingErrorIdStrings.RemoteRunspaceOpenUnknownState, state));
                        }

                        string fullyQualifiedErrorId = WSManTransportManagerUtils.GetFQEIDFromTransportError(
                            (transException != null) ? transException.ErrorCode : 0,
                            _defaultFQEID);
                        ErrorRecord errorRecord = new ErrorRecord(reason,
                             remoteRunspace, fullyQualifiedErrorId,
                                   ErrorCategory.OpenError, null, null,
                                        null, null, null, errorDetails, null);

                        Action<Cmdlet> errorWriter = delegate(Cmdlet cmdlet)
                        {
                            //
                            // In case of PSDirectException, we should output the precise error message
                            // in inner exception instead of the generic one in outer exception.
                            //
                            if ((errorRecord.Exception != null) && 
                                (errorRecord.Exception.InnerException != null))
                            {
                                PSDirectException ex = errorRecord.Exception.InnerException as PSDirectException;
                                if (ex != null)
                                {
                                    errorRecord = new ErrorRecord(errorRecord.Exception.InnerException, 
                                                                  errorRecord.FullyQualifiedErrorId, 
                                                                  errorRecord.CategoryInfo.Category, 
                                                                  errorRecord.TargetObject);
                                }
                            }
                        
                            cmdlet.WriteError(errorRecord);
                        };
                        if (writer.IsOpen)
                        {
                            writer.Write(errorWriter);
                        }

                        toDispose.Add(remoteRunspace);
                    }
                    break;

                case RunspaceState.Closed:
                    {
                        // The runspace was closed possibly because the user
                        // hit ctrl-C when runspaces were being opened or Dispose has been
                        // called when there are open runspaces
                        Uri connectionUri = WSManConnectionInfo.ExtractPropertyAsWsManConnectionInfo<Uri>(remoteRunspace.ConnectionInfo,
                            "ConnectionUri", null);
                        String message =
                            GetMessage(RemotingErrorIdStrings.RemoteRunspaceClosed,
                                        (connectionUri != null ) ?
                                        connectionUri.AbsoluteUri : string.Empty);

                        Action<Cmdlet> verboseWriter = delegate(Cmdlet cmdlet)
                        {
                            cmdlet.WriteVerbose(message);
                        };
                        if (writer.IsOpen)
                        {
                            writer.Write(verboseWriter);
                        }

                        // runspace may not have been opened in certain cases
                        // like when the max memory is set to 25MB, in such 
                        // cases write an error record
                        if (reason != null)
                        {
                            ErrorRecord errorRecord = new ErrorRecord(reason,
                                 "PSSessionStateClosed",
                                       ErrorCategory.OpenError, remoteRunspace);

                            Action<Cmdlet> errorWriter = delegate(Cmdlet cmdlet)
                            {
                                cmdlet.WriteError(errorRecord);
                            };
                            if (writer.IsOpen)
                            {
                                writer.Write(errorWriter);
                            }
                        }
                    }
                    break;

            }// switch

        } // HandleRunspaceStateChanged

        /// <summary>
        /// Creates the remote runspace objects when PSSession 
        /// parameter is specified
        /// It now supports PSSession based on VM/container connection info as well.
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly")]
        private List<RemoteRunspace> CreateRunspacesWhenRunspaceParameterSpecified()
        {
            List<RemoteRunspace> remoteRunspaces = new List<RemoteRunspace>();

            // validate the runspaces specfied before processing them.
            // The function will result in terminating errors, if any
            // validation failure is encountered
            ValidateRemoteRunspacesSpecified();

            int rsIndex = 0;
            foreach (PSSession remoteRunspaceInfo in remoteRunspaceInfos)
            {
                if (remoteRunspaceInfo == null || remoteRunspaceInfo.Runspace == null)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        new ArgumentNullException("PSSession"), "PSSessionArgumentNull",
                            ErrorCategory.InvalidArgument, null));
                }
                else
                {
                    // clone the object based on what's specified in the input parameter
                    try
                    {
                        RemoteRunspace remoteRunspace = (RemoteRunspace)remoteRunspaceInfo.Runspace;
                        RunspaceConnectionInfo newConnectionInfo = null;

                        if (remoteRunspace.ConnectionInfo is VMConnectionInfo)
                        {
                            newConnectionInfo = remoteRunspace.ConnectionInfo.InternalCopy();
                        }
                        else if (remoteRunspace.ConnectionInfo is ContainerConnectionInfo)
                        {
                            ContainerConnectionInfo newContainerConnectionInfo = remoteRunspace.ConnectionInfo.InternalCopy() as ContainerConnectionInfo;
                            newContainerConnectionInfo.CreateContainerProcess();
                            newConnectionInfo = newContainerConnectionInfo;
                        }
                        else
                        {
                            // WSMan case
                            WSManConnectionInfo originalWSManConnectionInfo = remoteRunspace.ConnectionInfo as WSManConnectionInfo;
                            WSManConnectionInfo newWSManConnectionInfo = null;
                        
                            if (null != originalWSManConnectionInfo)
                            {
                                newWSManConnectionInfo = originalWSManConnectionInfo.Copy();
                                newWSManConnectionInfo.EnableNetworkAccess = (newWSManConnectionInfo.EnableNetworkAccess || EnableNetworkAccess) ? true : false;
                                newConnectionInfo = newWSManConnectionInfo;
                            }
                            else
                            {
                                Uri connectionUri = WSManConnectionInfo.ExtractPropertyAsWsManConnectionInfo<Uri>(remoteRunspace.ConnectionInfo,
                                            "ConnectionUri", null);
                                string shellUri = WSManConnectionInfo.ExtractPropertyAsWsManConnectionInfo<string>(remoteRunspace.ConnectionInfo,
                                    "ShellUri", string.Empty);
                                newWSManConnectionInfo = new WSManConnectionInfo(connectionUri,
                                                                shellUri,
                                                                remoteRunspace.ConnectionInfo.Credential);
                                UpdateConnectionInfo(newWSManConnectionInfo);
                                newWSManConnectionInfo.EnableNetworkAccess = EnableNetworkAccess;
                                newConnectionInfo = newWSManConnectionInfo;
                            }
                        }
                        
                        RemoteRunspacePoolInternal rrsPool = remoteRunspace.RunspacePool.RemoteRunspacePoolInternal;
                        TypeTable typeTable = null;
                        if ((rrsPool != null) && 
                            (rrsPool.DataStructureHandler != null) &&
                            (rrsPool.DataStructureHandler.TransportManager != null))
                        {
                            typeTable = rrsPool.DataStructureHandler.TransportManager.Fragmentor.TypeTable;
                        }

                        // Create new remote runspace with name and Id.
                        int rsId;
                        string rsName = GetRunspaceName(rsIndex, out rsId);
                        RemoteRunspace newRemoteRunspace = new RemoteRunspace(
                            typeTable, newConnectionInfo, this.Host, this.SessionOption.ApplicationArguments,
                            rsName, rsId);

                        remoteRunspaces.Add(newRemoteRunspace);
                    }
                    catch (UriFormatException e)
                    {
                        PipelineWriter writer = stream.ObjectWriter;

                        ErrorRecord errorRecord = new ErrorRecord(e, "CreateRemoteRunspaceFailed",
                                ErrorCategory.InvalidArgument, remoteRunspaceInfo);

                        Action<Cmdlet> errorWriter = delegate(Cmdlet cmdlet)
                        {
                            cmdlet.WriteError(errorRecord);
                        };
                        writer.Write(errorWriter);
                    }
                }

                ++rsIndex;
            } // foreach

            return remoteRunspaces;

        } // CreateRunspacesWhenRunspaceParameterSpecified

        /// <summary>
        /// Creates the remote runspace objects when the URI parameter
        /// is specified
        /// </summary>
        private List<RemoteRunspace> CreateRunspacesWhenUriParameterSpecified()
        {
            List<RemoteRunspace> remoteRunspaces = new List<RemoteRunspace>();

            // parse the Uri to obtain information about the runspace
            // required
            for (int i = 0; i < ConnectionUri.Length; i++)
            {
                try
                {
                    WSManConnectionInfo connectionInfo = new WSManConnectionInfo();
                    connectionInfo.ConnectionUri = ConnectionUri[i];
                    connectionInfo.ShellUri = ConfigurationName;
                    if (CertificateThumbprint != null)
                    {
                        connectionInfo.CertificateThumbprint = CertificateThumbprint;
                    }
                    else
                    {
                        connectionInfo.Credential = Credential;
                    }

                    connectionInfo.AuthenticationMechanism = Authentication;
                    UpdateConnectionInfo(connectionInfo);

                    connectionInfo.EnableNetworkAccess = EnableNetworkAccess;

                    // Create new remote runspace with name and Id.
                    int rsId;
                    string rsName = GetRunspaceName(i, out rsId);
                    RemoteRunspace remoteRunspace = new RemoteRunspace(
                        Utils.GetTypeTableFromExecutionContextTLS(), connectionInfo, this.Host, 
                        this.SessionOption.ApplicationArguments, rsName, rsId);

                    Dbg.Assert(remoteRunspace != null,
                            "RemoteRunspace object created using URI is null");
                    
                    remoteRunspaces.Add(remoteRunspace);
                }
                catch (UriFormatException e)
                {
                    WriteErrorCreateRemoteRunspaceFailed(e, ConnectionUri[i]);
                }
                catch (InvalidOperationException e)
                {
                    WriteErrorCreateRemoteRunspaceFailed(e, ConnectionUri[i]);
                }
                catch (ArgumentException e)
                {
                    WriteErrorCreateRemoteRunspaceFailed(e, ConnectionUri[i]);
                }
                catch (NotSupportedException e)
                {
                    WriteErrorCreateRemoteRunspaceFailed(e, ConnectionUri[i]);
                }
            } // for...

            return remoteRunspaces;
        } // CreateRunspacesWhenUriParameterSpecified

        /// <summary>
        /// Creates the remote runspace objects when the ComputerName parameter
        /// is specified
        /// </summary>
        private List<RemoteRunspace> CreateRunspacesWhenComputerNameParameterSpecified()
        {
            List<RemoteRunspace> remoteRunspaces =
                new List<RemoteRunspace>();

            // Resolve all the machine names
            String[] resolvedComputerNames;

            ResolveComputerNames(ComputerName, out resolvedComputerNames);

            ValidateComputerName(resolvedComputerNames);

            // Do for each machine
            for (int i = 0; i < resolvedComputerNames.Length; i++)
            {
                try
                {
                    WSManConnectionInfo connectionInfo = null;
                    connectionInfo = new WSManConnectionInfo();
                    string scheme = UseSSL.IsPresent ? WSManConnectionInfo.HttpsScheme : WSManConnectionInfo.HttpScheme;
                    connectionInfo.ComputerName = resolvedComputerNames[i];
                    connectionInfo.Port = Port;
                    connectionInfo.AppName = ApplicationName;
                    connectionInfo.ShellUri = ConfigurationName;
                    connectionInfo.Scheme = scheme;
                    if (CertificateThumbprint != null)
                    {
                        connectionInfo.CertificateThumbprint = CertificateThumbprint;
                    }
                    else
                    {
                        connectionInfo.Credential = Credential;
                    } 
                    connectionInfo.AuthenticationMechanism = Authentication;
                    UpdateConnectionInfo(connectionInfo);

                    connectionInfo.EnableNetworkAccess = EnableNetworkAccess;

                    // Create new remote runspace with name and Id.
                    int rsId;
                    string rsName = GetRunspaceName(i, out rsId);
                    RemoteRunspace runspace = new RemoteRunspace(
                        Utils.GetTypeTableFromExecutionContextTLS(), connectionInfo, this.Host,
                        this.SessionOption.ApplicationArguments, rsName, rsId);
                    
                    remoteRunspaces.Add(runspace);
                }
                catch (UriFormatException e)
                {
                    PipelineWriter writer = stream.ObjectWriter;

                    ErrorRecord errorRecord = new ErrorRecord(e, "CreateRemoteRunspaceFailed",
                            ErrorCategory.InvalidArgument, resolvedComputerNames[i]);

                    Action<Cmdlet> errorWriter = delegate(Cmdlet cmdlet)
                    {
                        cmdlet.WriteError(errorRecord);
                    };
                    writer.Write(errorWriter);
                }
            }// end of for

            return remoteRunspaces;

        }// CreateRunspacesWhenComputerNameParameterSpecified

        /// <summary>
        /// Creates the remote runspace objects when the VMId or VMName parameter
        /// is specified
        /// </summary>
        private List<RemoteRunspace> CreateRunspacesWhenVMParameterSpecified()
        {
            int inputArraySize;
            bool isVMIdSet = false;
            int index;
            string command;
            Collection<PSObject> results;
            List<RemoteRunspace> remoteRunspaces = new List<RemoteRunspace>();

            if (ParameterSetName == PSExecutionCmdlet.VMIdParameterSet)
            {
                isVMIdSet = true;
                inputArraySize = this.VMId.Length;
                this.VMName = new string[inputArraySize];
                command = "Get-VM -Id $args[0]";
            }
            else
            {
                Dbg.Assert((ParameterSetName == PSExecutionCmdlet.VMNameParameterSet),
                           "Expected ParameterSetName == VMId or VMName");
                
                inputArraySize = this.VMName.Length;
                this.VMId = new Guid[inputArraySize];
                command = "Get-VM -Name $args";
            }

            for (index = 0; index < inputArraySize; index++)
            {
                try
                {
                    results = this.InvokeCommand.InvokeScript(
                        command, false, PipelineResultTypes.None, null, 
                        isVMIdSet ? this.VMId[index].ToString() : this.VMName[index]);
                }
                catch (CommandNotFoundException)
                {
                    ThrowTerminatingError(
                        new ErrorRecord(
                            new ArgumentException(RemotingErrorIdStrings.HyperVModuleNotAvailable),
                            PSRemotingErrorId.HyperVModuleNotAvailable.ToString(),
                            ErrorCategory.NotInstalled,
                            null));
            
                    return null;
                }

                // handle invalid input
                if (results.Count != 1)
                {
                    if (isVMIdSet)
                    {
                        this.VMName[index] = string.Empty;

                        WriteError(
                            new ErrorRecord(
                                new ArgumentException(GetMessage(RemotingErrorIdStrings.InvalidVMIdNotSingle, 
                                                                 this.VMId[index].ToString(null))),
                                PSRemotingErrorId.InvalidVMIdNotSingle.ToString(),
                                ErrorCategory.InvalidArgument,
                                null));

                        continue;
                    }
                    else
                    {
                        this.VMId[index] = Guid.Empty;

                        WriteError(
                            new ErrorRecord(
                                new ArgumentException(GetMessage(RemotingErrorIdStrings.InvalidVMNameNotSingle, 
                                                                 this.VMName[index])),
                                PSRemotingErrorId.InvalidVMNameNotSingle.ToString(),
                                ErrorCategory.InvalidArgument,
                                null));

                        continue;
                    }
                }
                else
                {
                    this.VMId[index] = (Guid)results[0].Properties["VMId"].Value;
                    this.VMName[index] = (string)results[0].Properties["VMName"].Value;
                }

                // create helper objects for VM GUIDs or names
                RemoteRunspace runspace = null;
                VMConnectionInfo connectionInfo;
                int rsId;
                string rsName = GetRunspaceName(index, out rsId);
                
                try
                {
                    connectionInfo = new VMConnectionInfo(this.Credential, this.VMId[index], this.VMName[index], this.ConfigurationName);
                
                    runspace = new RemoteRunspace(Utils.GetTypeTableFromExecutionContextTLS(),
                        connectionInfo, this.Host, null, rsName, rsId);
                
                    remoteRunspaces.Add(runspace);
                }
                catch (InvalidOperationException e)
                {
                    ErrorRecord errorRecord = new ErrorRecord(e,
                        "CreateRemoteRunspaceForVMFailed", 
                        ErrorCategory.InvalidOperation,
                        null);
                    
                    WriteError(errorRecord);
                }
                catch (ArgumentException e)
                {
                    ErrorRecord errorRecord = new ErrorRecord(e,
                        "CreateRemoteRunspaceForVMFailed", 
                        ErrorCategory.InvalidArgument,
                        null);
                    
                    WriteError(errorRecord);
                }
            }

            ResolvedComputerNames = this.VMName;

            return remoteRunspaces;
        }// CreateRunspacesWhenVMParameterSpecified

        /// <summary>
        /// Creates the remote runspace objects when the ContainerId or ContainerName parameter
        /// is specified
        /// </summary>
        private List<RemoteRunspace> CreateRunspacesWhenContainerParameterSpecified()
        {
            string[] inputArray;
            bool isContainerIdSet = false;
            int index = 0;
            List<string> resolvedNameList = new List<string>();
            List<RemoteRunspace> remoteRunspaces = new List<RemoteRunspace>();
            
            if (ParameterSetName == PSExecutionCmdlet.ContainerIdParameterSet)
            {
                inputArray = ContainerId;
                isContainerIdSet = true;
            }
            else
            {
                Dbg.Assert((ParameterSetName == PSExecutionCmdlet.ContainerNameParameterSet),
                           "Expected ParameterSetName == ContainerId or ContainerName");
                
                inputArray = ContainerName;
            }
                
            foreach (var input in inputArray)
            {
                //
                // Create helper objects for container ID or name.
                //
                RemoteRunspace runspace = null;
                ContainerConnectionInfo connectionInfo = null;
                int rsId;
                string rsName = GetRunspaceName(index, out rsId);
                index++;

                try
                {
                    //
                    // Hyper-V container uses Hype-V socket as transport.
                    // Windows Server container uses named pipe as transport.
                    //
                    if (isContainerIdSet)
                    {
                        connectionInfo = ContainerConnectionInfo.CreateContainerConnectionInfoById(input, RunAsAdministrator.IsPresent, this.ConfigurationName);
                    }
                    else
                    {
                        connectionInfo = ContainerConnectionInfo.CreateContainerConnectionInfoByName(input, RunAsAdministrator.IsPresent, this.ConfigurationName);
                    }
                  
                    resolvedNameList.Add(connectionInfo.ComputerName);
                    
                    connectionInfo.CreateContainerProcess();
                    
                    runspace = new RemoteRunspace(Utils.GetTypeTableFromExecutionContextTLS(),
                        connectionInfo, this.Host, null, rsName, rsId);
                    
                    remoteRunspaces.Add(runspace);
                }
                catch (InvalidOperationException e)
                {
                    ErrorRecord errorRecord = new ErrorRecord(e,
                        "CreateRemoteRunspaceForContainerFailed", 
                        ErrorCategory.InvalidOperation,
                        null);
                    
                    WriteError(errorRecord);
                    continue;
                }
                catch (ArgumentException e)
                {
                    ErrorRecord errorRecord = new ErrorRecord(e,
                        "CreateRemoteRunspaceForContainerFailed", 
                        ErrorCategory.InvalidArgument,
                        null);
                    
                    WriteError(errorRecord);
                    continue;
                }
                catch (Exception e)
                {
                    ErrorRecord errorRecord = new ErrorRecord(e,
                        "CreateRemoteRunspaceForContainerFailed", 
                        ErrorCategory.InvalidOperation,
                        null);
                    
                    WriteError(errorRecord);
                    continue;
                }                
            }

            ResolvedComputerNames = resolvedNameList.ToArray();

            return remoteRunspaces;
        }// CreateRunspacesWhenContainerParameterSpecified

        /// <summary>
        /// Helper method to either get a user supplied runspace/session name
        /// or to generate one along with a unique Id.
        /// </summary>
        /// <param name="rsIndex">Runspace name array index.</param>
        /// <param name="rsId">Runspace Id.</param>
        /// <returns>Runspace name.</returns>
        private string GetRunspaceName(int rsIndex, out int rsId)
        {
            // Get a unique session/runspace Id and default Name.
            string rsName = PSSession.GenerateRunspaceName(out rsId);

            // If there is a friendly name for the runspace, we need to pass it to the
            // runspace pool object, which in turn passes it on to the server during
            // construction.  This way the friendly name can be returned when querying
            // the sever for disconnected sessions/runspaces.
            if (names != null && rsIndex < names.Length)
            {
                rsName = names[rsIndex];
            }

            return rsName;
        }

        /// <summary>
        /// Internal dispose method which does the actual
        /// dispose operations and finalize suppressions
        /// </summary>
        /// <param name="disposing">Whether method is called 
        /// from Dispose or destructor</param>
        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                throttleManager.Dispose();

                // wait for all runspace operations to be complete
                operationsComplete.WaitOne();
                operationsComplete.Dispose();

                throttleManager.ThrottleComplete -= new EventHandler<EventArgs>(HandleThrottleComplete);
                throttleManager = null;

                foreach (RemoteRunspace remoteRunspace in toDispose)
                {
                    remoteRunspace.Dispose();
                }

                // Dispose all open operation objects, to remove runspace event callback.
                foreach (List<IThrottleOperation> operationList in this.allOperations)
                {
                    foreach (OpenRunspaceOperation operation in operationList)
                    {
                        operation.Dispose();
                    }
                }

                stream.Dispose();
            }

        } // Dispose

        /// <summary>
        /// Handles the throttling complete event of the throttle manager
        /// </summary>
        /// <param name="sender">sender of this event</param>
        /// <param name="eventArgs"></param>
        private void HandleThrottleComplete(object sender, EventArgs eventArgs)
        {
            // all operations are complete close the stream
            stream.ObjectWriter.Close();

            operationsComplete.Set();

        } // HandleThrottleComplete

        /// <summary>
        /// Writes an error record specifying that creation of remote runspace
        /// failed
        /// </summary>
        /// <param name="e">exception which is causing this error record
        /// to be written</param>
        /// <param name="uri">Uri which caused this exception</param>
        private void WriteErrorCreateRemoteRunspaceFailed(Exception e, Uri uri)
        {
            Dbg.Assert(e is UriFormatException || e is InvalidOperationException ||
                       e is ArgumentException || e is NotSupportedException,
                       "Exception has to be of type UriFormatException or InvalidOperationException or ArgumentException or NotSupportedException");

            PipelineWriter writer = stream.ObjectWriter;

            ErrorRecord errorRecord = new ErrorRecord(e, "CreateRemoteRunspaceFailed",
                ErrorCategory.InvalidArgument, uri);

            Action<Cmdlet> errorWriter = delegate(Cmdlet cmdlet)
            {
                cmdlet.WriteError(errorRecord);
            };
            writer.Write(errorWriter);
        } // WriteErrorCreateRemoteRunspaceFailed

        #endregion Private Methods

        #region Private Members

        private ThrottleManager throttleManager = new ThrottleManager();
        private ObjectStream stream = new ObjectStream();
        // event that signals that all operations are 
        // complete (including closing if any)
        private ManualResetEvent operationsComplete = new ManualResetEvent(true);
        // the initial state is true because when no 
        // operations actually take place as in case of a 
        // parameter binding exception, then Dispose is
        // called. Since Dispose waits on this handler
        // it is set to true initially and is Reset() in
        // BeginProcessing()

        // list of runspaces to dispose
        private List<RemoteRunspace> toDispose = new List<RemoteRunspace>();

        // List of runspace connect operations.  Need to keep for cleanup.
        private Collection<List<IThrottleOperation>> allOperations = new Collection<List<IThrottleOperation>>();

        // Default FQEID.
        private string _defaultFQEID = "PSSessionOpenFailed";

        #endregion Private Members

    }// NewRunspace

    #region Helper Classes

    /// <summary>
    /// Class that implements the IThrottleOperation in turn wrapping the
    /// opening of a runspace asynchronously within it
    /// </summary>
    internal class OpenRunspaceOperation : IThrottleOperation, IDisposable
    {
        // Member variables to ensure that the ThrottleManager gets StartComplete
        // or StopComplete called only once per Start or Stop operation.
        private bool startComplete;
        private bool stopComplete;

        private object _syncObject = new object();

        internal RemoteRunspace OperatedRunspace
        {
            get
            {
                return runspace;
            }
        }
        private RemoteRunspace runspace;

        internal OpenRunspaceOperation(RemoteRunspace runspace)
        {
            this.startComplete = true;
            this.stopComplete = true;
            this.runspace = runspace;
            this.runspace.StateChanged +=
                new EventHandler<RunspaceStateEventArgs>(HandleRunspaceStateChanged);
        }

        /// <summary>
        /// Opens the runspace asynchronously
        /// </summary>
        internal override void StartOperation()
        {
            lock (_syncObject)
            {
                this.startComplete = false;
            }
            runspace.OpenAsync();
        } // StartOperation

        /// <summary>
        /// Closes the runspace already opened asynchronously
        /// </summary>        
        internal override void StopOperation()
        {
            OperationStateEventArgs operationStateEventArgs = null;

            lock (_syncObject)
            {
                // Ignore stop operation if start operation has completed.
                if (this.startComplete)
                {
                    this.stopComplete = true;
                    this.startComplete = true;
                    operationStateEventArgs = new OperationStateEventArgs();
                    operationStateEventArgs.BaseEvent = new RunspaceStateEventArgs(runspace.RunspaceStateInfo);
                    operationStateEventArgs.OperationState = OperationState.StopComplete;
                }
                else
                {
                    this.stopComplete = false;
                }
            }

            if (operationStateEventArgs != null)
            {
                FireEvent(operationStateEventArgs);
            }
            else
            {
                runspace.CloseAsync();
            }
        }

        // OperationComplete event handler uses an internal collection of event handler 
        // callbacks for two reasons:
        //  a) To ensure callbacks are made in list order (first added, first called).
        //  b) To ensure all callbacks are fired by manually invoking callbacks and handling 
        //     any exceptions thrown on this thread. (ThrottleManager will hang if it doesn't 
        //     get a start/stop complete callback).
        private List<EventHandler<OperationStateEventArgs>> _internalCallbacks = new List<EventHandler<OperationStateEventArgs>>();
        internal override event EventHandler<OperationStateEventArgs> OperationComplete
        {
            add
            {
                lock (_internalCallbacks)
                {
                    _internalCallbacks.Add(value);
                }
            }
            remove
            {
                lock (_internalCallbacks)
                {
                    _internalCallbacks.Remove(value);
                }
            }
        }

        /// <summary>
        /// Handler for handling runspace state changed events. This method will be
        /// registered in the StartOperation and StopOperation methods. This handler
        /// will in turn invoke the OperationComplete event for all events that are 
        /// necesary - Opened, Closed, Disconnected, Broken. It will ignore all other state 
        /// changes.
        /// </summary>
        /// <remarks>
        /// There are two problems that need to be handled.
        /// 1) We need to make sure that the ThrottleManager StartComplete and StopComplete
        ///    operation events are called or the ThrottleManager will never end (hang).
        /// 2) The HandleRunspaceStateChanged event handler remains in the Runspace
        ///    StateChanged event call chain until this object is disposed.  We have to
        ///    disallow the HandleRunspaceStateChanged event from running and throwing
        ///    an exception since this prevents other event handlers in the chain from
        ///    being called.
        /// </remarks>
        /// <param name="source">Source of this event</param>
        /// <param name="stateEventArgs">object describing state information of the
        /// runspace</param>
        private void HandleRunspaceStateChanged(object source, RunspaceStateEventArgs stateEventArgs)
        {
            // Disregard intermediate states.
            switch (stateEventArgs.RunspaceStateInfo.State)
            {
                case RunspaceState.Opening:
                case RunspaceState.BeforeOpen:
                case RunspaceState.Closing:
                    return;
            }

            OperationStateEventArgs operationStateEventArgs = null;
            lock (_syncObject)
            {
                // We must call OperationComplete ony *once* for each Start/Stop operation.
                if (!this.stopComplete)
                {
                    // Note that the StopComplete callback removes *both* the Start and Stop
                    // operations from their respective queues.  So update the member vars
                    // accordingly.
                    this.stopComplete = true;
                    this.startComplete = true;
                    operationStateEventArgs = new OperationStateEventArgs();
                    operationStateEventArgs.BaseEvent = stateEventArgs;
                    operationStateEventArgs.OperationState = OperationState.StopComplete;
                }
                else if (!this.startComplete)
                {
                    this.startComplete = true;
                    operationStateEventArgs = new OperationStateEventArgs();
                    operationStateEventArgs.BaseEvent = stateEventArgs;
                    operationStateEventArgs.OperationState = OperationState.StartComplete;
                }
            }

            if (operationStateEventArgs != null)
            {
                // Fire callbacks in list order.
                FireEvent(operationStateEventArgs);
            }
        }

        private void FireEvent(OperationStateEventArgs operationStateEventArgs)
        {
            EventHandler<OperationStateEventArgs>[] copyCallbacks;
            lock (_internalCallbacks)
            {
                copyCallbacks = new EventHandler<OperationStateEventArgs>[_internalCallbacks.Count];
                _internalCallbacks.CopyTo(copyCallbacks);
            }
            foreach (var callbackDelegate in copyCallbacks)
            {
                // Ensure all callbacks get called to prevent ThrottleManager hang.
                try
                {
                    callbackDelegate.SafeInvoke(this, operationStateEventArgs);
                }
                catch (Exception e)
                {
                    CommandProcessorBase.CheckForSevereException(e);
                }
            }
        }

        /// <summary>
        /// Implements IDisposable.
        /// </summary>
        public void Dispose()
        {
            // Must remove the event callback from the new runspace or it will block other event
            // handling by throwing an exception on the event thread.
            this.runspace.StateChanged -= HandleRunspaceStateChanged;

            GC.SuppressFinalize(this);
        }

    } // OpenRunspaceOperation

    #endregion Helper Classes

}//End namespace