﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

using Autodesk.DesignScript.Interfaces;

using Dynamo.Core.Threading;
using Dynamo.DSEngine;
using Dynamo.Interfaces;
using Dynamo.Models;
using Dynamo.Selection;

using Microsoft.Practices.Prism.ViewModel;

namespace Dynamo
{
    /// <summary>
    /*
    * The VisualizationManager is reponsible for handling events from the DynamoModel
    * that result in the updating of RenderPackages on NodeModels. After these updates,
    * the VisualizationManager raises events which can be handled by the view, to
    * update rendered geometry. 
    * 
    * The sequence diagram is as follows:
    * 
    *   DynamoModel               Visualization Manager                       Watch3D
    *        |                             |                                      |
    *   EvaluationCompleted--------------->x                                      |
    *        |                     RequestAllNodesVisualsUpdate                   |
    *        |                     RequestNodeVisualUpdateAsync                   |
    *        |                     NotifyRenderPackagesReadyAsyncTask             |
    *        |                     OnNodeModelRenderPackagesReady                 |
    *        |                     RenderComplete-------------------------------->x
    *        |                             |                               RenderCompleteHandler                 
    *        |                             |                               RequestBranchUpdate
    *        |                     AggregateRenderPackageAsyncTask<---------------x 
    *        |                     ResultsReadyToVisualize----------------------->x
    *        |                             |                               ResultsReadyToVisualizeHandler
    *        |                             |                               Render
    */ 
    /// </summary>
    public class VisualizationManager : NotificationObject, IVisualizationManager
    {
        #region private members

        private string alternateContextName = "Host";
        private bool drawToAlternateContext = true;
        private bool updatingPaused;
        protected readonly DynamoModel dynamoModel;
        private readonly List<RenderPackage> currentTaggedPackages = new List<RenderPackage>();
        private bool alternateDrawingContextAvailable;
        
        #endregion

        #region public properties

        /// <summary>
        /// Is another context available for drawing?
        /// This property can be queried indirectly by the view to enable or disable
        /// UI functionality based on whether an alternate drawing context is available.
        /// </summary>
        public bool AlternateDrawingContextAvailable
        {
            get { return alternateDrawingContextAvailable; }
            set
            {
                alternateDrawingContextAvailable = value;
                RaisePropertyChanged("AlternateDrawingContextAvailable");
            }
        }

        /// <summary>
        /// Should we draw to the alternate context if it is available?
        /// </summary>
        public bool DrawToAlternateContext
        {
            get { return drawToAlternateContext; }
            set
            {
                if (value == false)
                {
                    //if the present value has us drawing to the alternate
                    //context and we would like to stop doing so, we need 
                    //to trigger an event requesting alternate contexts
                    //to drop their visualizations
                    if (drawToAlternateContext)
                    {
                        drawToAlternateContext = value;
                        OnRequestAlternateContextClear();
                    }
                }
                else
                {
                    //we would like to reenable drawing to an alternate context.
                    //trigger the standard visualization complete event
                    if (!drawToAlternateContext)
                    {
                        drawToAlternateContext = value;
                        OnRenderComplete();
                    }
                }
                RaisePropertyChanged("DrawToAlternateContext");
            }
        }

        /// <summary>
        /// The name of the alternate context for use in the UI.
        /// </summary>
        public string AlternateContextName
        {
            get { return alternateContextName; }
            set { alternateContextName = value; }
        }

        #endregion

        #region events

        /// <summary>
        /// An event triggered when there are results to visualize.
        /// </summary>
        public event Action<VisualizationEventArgs> ResultsReadyToVisualize;
        protected virtual void OnResultsReadyToVisualize(VisualizationEventArgs args)
        {
            if (ResultsReadyToVisualize != null)
                ResultsReadyToVisualize(args);
        }

        /// <summary>
        /// An event triggered on the completion of visualization update.
        /// </summary>
        public event Action RenderComplete;
        protected virtual void OnRenderComplete()
        {
            if (RenderComplete != null)
                RenderComplete();
        }

        /// <summary>
        /// An event triggered when want any alternate drawing contexts to be cleared.
        /// </summary>
        public event Action RequestAlternateContextClear;
        protected virtual void OnRequestAlternateContextClear()
        {
            if (RequestAlternateContextClear != null)
                RequestAlternateContextClear();
        }

        #endregion

        #region constructors

        public VisualizationManager(DynamoModel model)
        {
            dynamoModel = model;

            dynamoModel.WorkspaceClearing += Stop;
            dynamoModel.WorkspaceCleared += ClearVisualizations;

            dynamoModel.WorkspaceAdded += WorkspaceAdded;
            dynamoModel.WorkspaceRemoved += WorkspaceRemoved;

            dynamoModel.DeletionStarted += Stop;
            dynamoModel.DeletionComplete += StartAndUpdate;

            dynamoModel.CleaningUp += Clear;

            dynamoModel.EvaluationCompleted += RequestAllNodesVisualsUpdate;
            dynamoModel.RequestsRedraw += RequestAllNodesVisualsUpdate;

            DynamoSelection.Instance.Selection.CollectionChanged += SelectionChanged;

            Start();
        }

        #endregion

        #region public methods

        /// <summary>
        /// Stop the visualization manager.
        /// When the visualization manager is stopped, no rendering
        /// will occur.
        /// </summary>
        public void Stop()
        {
#if DEBUG
            Debug.WriteLine("Visualization manager stopped.");
#endif
            updatingPaused = true;
        }

        /// <summary>
        /// Start the visualization manager.
        /// When the visualization manager is started, the visualization
        /// manager begins rendering again.
        /// </summary>
        /// <param name="update">If update is True, after starting, the
        /// visualization manager will immediately update render pacakage. The
        /// default is False.</param>
        public void Start(bool update = false)
        {
#if DEBUG
            Debug.WriteLine("Visualization manager started.");
#endif
            updatingPaused = false;
            if(update)
                OnRenderComplete();
        }

        /// <summary>
        /// Display a label for one or several render packages 
        /// based on the paths of those render packages.
        /// </summary>
        /// <param name="path"></param>
        public void TagRenderPackageForPath(string path)
        {
            var packages = new List<RenderPackage>();

            //This also isn't thread safe
            foreach (var node in dynamoModel.CurrentWorkspace.Nodes)
            {
                lock (node.RenderPackagesMutex)
                {
                    //Note(Luke): this seems really inefficent, it's doing an O(n) search for a tag
                    //This is also a target for memory optimisation

                    packages.AddRange(
                        node.RenderPackages.Where(x => x.Tag == path || x.Tag.Contains(path + ":"))
                            .Cast<RenderPackage>());
                }
            }

            if (packages.Any())
            {
                //clear any labels that might have been drawn on this
                //package already and add the one we want
                if (currentTaggedPackages.Any())
                {
                    currentTaggedPackages.ForEach(x => x.DisplayLabels = false);
                    currentTaggedPackages.Clear();
                }

                packages.ToList().ForEach(x => x.DisplayLabels = true);
                currentTaggedPackages.AddRange(packages);

                var allPackages = new List<RenderPackage>();

                foreach (var node in dynamoModel.CurrentWorkspace.Nodes)
                {
                    lock (node.RenderPackagesMutex)
                    {
                        allPackages.AddRange(
                            node.RenderPackages.Where(x => ((RenderPackage)x).IsNotEmpty())
                                .Cast<RenderPackage>());
                    }
                }

                OnResultsReadyToVisualize(
                    new VisualizationEventArgs(allPackages, Guid.Empty));
            }
        }

        /// <summary>
        /// Request updated visuals for a branch of the graph.
        /// </summary>
        /// <param name="node">The node whose branch you want updated visuals for, 
        /// or null to return everything.</param>
        public void RequestBranchUpdate(NodeModel node)
        {
            var scheduler = dynamoModel.Scheduler;
            if (scheduler == null) // Shutdown has begun.
                return;

            // Schedule a AggregateRenderPackageAsyncTask here so that the 
            // background geometry preview gets refreshed.
            // 
            var task = new AggregateRenderPackageAsyncTask(scheduler);
            task.Initialize(dynamoModel.CurrentWorkspace, node);
            task.Completed += OnRenderPackageAggregationCompleted;
            scheduler.ScheduleForExecution(task);
        }

        /// <summary>
        /// Create an IRenderPackage object provided an IGraphicItem
        /// </summary>
        /// <param name="gItem">An IGraphicItem object to tessellate.</param>
        /// <returns>An IRenderPackage object.</returns>
        public IRenderPackage CreateRenderPackageFromGraphicItem(IGraphicItem gItem)
        {
            var renderPackage = new RenderPackage();
            gItem.Tessellate(renderPackage, -1.0, dynamoModel.MaxTesselationDivisions);
            renderPackage.ItemsCount++;
            return renderPackage;
        }

        /// <summary>
        /// Unhook all event handlers.
        /// </summary>
        public void Dispose()
        {
            DynamoSelection.Instance.Selection.CollectionChanged -= SelectionChanged;

            UnregisterEventListeners();

            dynamoModel.WorkspaceCleared -= ClearVisualizations;

            dynamoModel.WorkspaceAdded -= WorkspaceAdded;
            dynamoModel.WorkspaceRemoved -= WorkspaceRemoved;

            dynamoModel.DeletionStarted -= Stop;
            dynamoModel.DeletionComplete -= StartAndUpdate;

            dynamoModel.CleaningUp -= Clear;

            dynamoModel.EvaluationCompleted -= RequestAllNodesVisualsUpdate;
            dynamoModel.RequestsRedraw -= RequestAllNodesVisualsUpdate;

        }

        #endregion

        #region private event handlers

        private void StartAndUpdate(object sender, EventArgs e)
        {
            Start(true);
        }

        private void WorkspaceAdded(WorkspaceModel model)
        {
            var workspace = model as HomeWorkspaceModel;
            if (workspace == null) return;

            workspace.NodeAdded += NodeAddedToHomeWorkspace;
            workspace.NodeRemoved += NodeRemovedFromHomeWorkspace;

            foreach (var node in workspace.Nodes)
                NodeAddedToHomeWorkspace(node);
        }

        private void WorkspaceRemoved(WorkspaceModel model)
        {
            var workspace = model as HomeWorkspaceModel;
            if (workspace == null) return;

            workspace.NodeAdded -= NodeAddedToHomeWorkspace;
            workspace.NodeRemoved -= NodeRemovedFromHomeWorkspace;

            foreach (var node in workspace.Nodes)
                NodeRemovedFromHomeWorkspace(node);
        }

        private void NodeRemovedFromHomeWorkspace(NodeModel node)
        {
            node.PropertyChanged -= NodePropertyChanged;
        }

        private void NodeAddedToHomeWorkspace(NodeModel node)
        {
            node.PropertyChanged += NodePropertyChanged;
        }

        private void NodePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (updatingPaused) return;

            switch (e.PropertyName)
            {
                case "IsVisible":
                case "IsUpstreamVisible":
                case "DisplayLabels":
                    RequestNodeVisualUpdateAsync(sender as NodeModel);
                    break;
            }
        }

        private void SelectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (updatingPaused)
                return;

            OnRenderComplete();
        }

        #endregion

        #region private methods

        private void UnregisterEventListeners()
        {
            foreach (var n in dynamoModel.CurrentWorkspace.Nodes)
                n.PropertyChanged -= NodePropertyChanged;
        }

        private void RequestAllNodesVisualsUpdate(object sender, EventArgs e)
        {
            // do nothing if it has come on EvaluationCompleted
            // and no evaluation was
            var args = e as EvaluationCompletedEventArgs;
            if (args != null && !args.EvaluationTookPlace)
                return;

            RequestNodeVisualUpdateAsync(null);
        }

        private void RequestNodeVisualUpdateAsync(NodeModel nodeModel)
        {
            if (nodeModel != null)
            {
                // Visualization update for a given node is desired.
                nodeModel.RequestVisualUpdateAsync(
                    dynamoModel.Scheduler,
                    dynamoModel.EngineController,
                    dynamoModel.MaxTesselationDivisions);
            }
            else
            {
                // Get each node in workspace to update their visuals.
                foreach (var node in dynamoModel.CurrentWorkspace.Nodes)
                {
                    node.RequestVisualUpdateAsync(
                        dynamoModel.Scheduler,
                        dynamoModel.EngineController,
                        dynamoModel.MaxTesselationDivisions);
                }
            }

            // Schedule a NotifyRenderPackagesReadyAsyncTask here so that when 
            // render packages of all the NodeModel objects are generated, the 
            // VisualizationManager gets notified.
            // 
            var scheduler = dynamoModel.Scheduler;
            var notifyTask = new NotifyRenderPackagesReadyAsyncTask(scheduler);
            notifyTask.Completed += OnNodeModelRenderPackagesReady;
            scheduler.ScheduleForExecution(notifyTask);
        }

        private void OnNodeModelRenderPackagesReady(AsyncTask asyncTask)
        {
            // By design the following method is invoked on the context of 
            // ISchedulerThread, if access to any UI element is desired within
            // the method, dispatch those actions on UI dispatcher *inside* the
            // method, *do not* dispatch the following call here as derived 
            // handler may need it to remain on the ISchedulerThread's context.
            // 

            // Fire event to tell render targets to request their visuals
            OnRenderComplete();

            // Call overridden method on visualization manager to
            // process whatever internal logic there is around
            // drawing a visualization.
            HandleRenderPackagesReadyCore();
        }

        private void OnRenderPackageAggregationCompleted(AsyncTask asyncTask)
        {
            var task = asyncTask as AggregateRenderPackageAsyncTask;
            var rps = new List<RenderPackage>();
            rps.AddRange(task.NormalRenderPackages.Cast<RenderPackage>());
            rps.AddRange(task.SelectedRenderPackages.Cast<RenderPackage>());

            var e = new VisualizationEventArgs(rps, task.NodeId);
            OnResultsReadyToVisualize(e);
        }

        private void Clear()
        {
            // Send along an empty render set to clear all visualizations.
            Stop();
            OnResultsReadyToVisualize(new VisualizationEventArgs(new List<RenderPackage>{}, Guid.Empty));
        }

        private void ClearVisualizations(object sender, EventArgs e)
        {
            Clear();
        }

        #endregion

        #region protected methods

        protected virtual void HandleRenderPackagesReadyCore()
        {
            // Default visualization manager does nothing here.
        }

        #endregion
    }

    /// <summary>
    /// The VisualizationEventArgs class is used to convey RenderPackage
    /// and identity data to handlers of the ResultsReadyToVisualize event
    /// on the VisualizationManager.
    /// </summary>
    public sealed class VisualizationEventArgs : EventArgs
    {
        /// <summary>
        /// A list of render packages corresponding to a branch or a whole graph.
        /// </summary>
        public IEnumerable<IRenderPackage> Packages { get; private set; }

        /// <summary>
        /// The id of the view for which the description belongs.
        /// </summary>
        public Guid Id { get; protected set; }

        public VisualizationEventArgs(IEnumerable<IRenderPackage> description, Guid viewId)
        {
            Packages = description;
            Id = viewId; 
        }
    }

}