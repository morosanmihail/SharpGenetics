﻿using Accord.MachineLearning;
using Accord.MachineLearning.DecisionTrees;
using Accord.Math;
using Accord.Statistics;
using SharpGenetics.BaseClasses;
using SharpGenetics.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Accord.MachineLearning.Performance;
using Accord.MachineLearning.DecisionTrees.Learning;
using Accord.Math.Optimization.Losses;

namespace SharpGenetics.Predictor
{
    [DataContract]
    public class DecisionTreeC45Predictor : ResultPredictor<List<double>, List<double>>
    {
        [DataMember]
        public WeightedTrainingSet NetworkTrainingData;

        DecisionTree Tree = null;

        byte[] NetworkSerializeValue;

        [DataMember]
        public byte[] NetworkSerialize
        {
            get
            {
                if (Tree == null)
                    return null;
                return Accord.IO.Serializer.Save(Tree);
            }
            set
            {
                NetworkSerializeValue = value;
            }
        }

        [DataMember]
        double Median = 0;
        [DataMember]
        double FirstQuart = 0;
        [DataMember]
        double ThirdQuart = 0;

        [DataMember]
        [ImportantParameter("extra_Predictor_C45_ThresholdClass", "Threshold Class For Accepting Predictions", 0, 3, 2)]
        public int ThresholdClass { get; set; }

        [DataMember]
        [ImportantParameter("extra_Predictor_C45_TrainingDataHigh", "Training Data High Values Capacity", 0, 200, 25)]
        public int TrainingDataHighCount { get; set; }

        [DataMember]
        [ImportantParameter("extra_Predictor_C45_TrainingDataLow", "Training Data Low Values Capacity", 0, 200, 25)]
        public int TrainingDataLowCount { get; set; }

        [DataMember]
        [ImportantParameter("extra_Predictor_C45_TrainingDataTotal", "Training Data Total Capacity", 0, 200, 100)]
        public int TrainingDataTotalCount { get; set; }

        [DataMember]
        [ImportantParameter("extra_Predictor_C45_TotalClasses", "Number of Output Classes", 0, 20, 4)]
        public int TotalClasses { get; set; }

        [DataMember]
        public double NetworkAccuracy = -1;

        public DecisionTreeC45Predictor(RunParameters Parameters, int RandomSeed)
        {
            Accord.Math.Random.Generator.Seed = RandomSeed;

            PredictorHelper.ApplyPropertiesToPredictor<DecisionTreeC45Predictor>(this, Parameters);

            NetworkTrainingData = new WeightedTrainingSet(TrainingDataHighCount, TrainingDataLowCount, TrainingDataTotalCount);

            Setup();
        }

        public override void Setup()
        {
            lock (NetworkLock)
            {
                if (Tree == null)
                {
                    if (NetworkSerializeValue != null)
                    {
                        Tree = Accord.IO.Serializer.Load<DecisionTree>(NetworkSerializeValue);
                    } 
                }
            }
        }

        public void AddInputOutputToData(List<double> ParamsToSend, List<double> Outputs)
        {
            lock (NetworkLock)
            {
                NetworkTrainingData.AddIndividualToTrainingSet(new InputOutputPair(ParamsToSend, Outputs));
            }
        }

        int ClassifyOutputs(List<double> Output, double FirstQuart, double Median, double ThirdQuart)
        {
            double Sum = Output.Sum();
            if (Sum < FirstQuart)
            {
                return 0;
            }
            if (Sum < Median)
            {
                double ratio = 1 + (Sum - FirstQuart) * (int)(((TotalClasses - 1) / 2)) / (Median - FirstQuart);
                return (int)ratio;
            }
            if (Sum < ThirdQuart)
            {
                double ratio = 1 + (int)((TotalClasses-1) / 2) + (Sum - Median) * (int)(((TotalClasses - 1) / 2)) / (ThirdQuart - Median);
                return (int)ratio;
            }
            else
                return TotalClasses - 1;
        }

        public override void AfterGeneration(List<PopulationMember> Population, int Generation, double BaseScoreError)
        {
            lock (NetworkLock)
            {
                foreach (var Indiv in Population)
                {
                    if (!Indiv.Predicted && Indiv.Fitness >= 0)
                    {
                        AddInputOutputToData(Indiv.Vector, Indiv.ObjectivesFitness);
                    }
                }

                var AllFitnesses = Population.Select(i => i.Fitness).ToArray();

                FirstQuart = 0;
                ThirdQuart = 0;
                Median = AllFitnesses.Quartiles(out FirstQuart, out ThirdQuart, false);
            }
        }

        DecisionTree GenerateBestTree(double[][] input, int[] output)
        {
            int bestJoin = 13;
            int bestHeight = 15;

            var bestTeacher = new C45Learning
            {
                Join = bestJoin,
                MaxHeight = bestHeight,
            };
            
            return bestTeacher.Learn(input, output);
        }

        public override void AtStartOfGeneration(List<PopulationMember> Population, RunMetrics RunMetrics, int Generation)
        {
            var TrainingData = NetworkTrainingData.GetAllValues();

            if (TrainingData.Count < TrainingDataTotalCount)
            {
                return;
            }

            TrainingData.Shuffle();

            double[][] input = TrainingData.Take((int)(TrainingData.Count * 0.8)).Select(e => e.Inputs.ToArray()).ToArray();
            int[] output = TrainingData.Take((int)(TrainingData.Count * 0.8)).Select(e => ClassifyOutputs(e.Outputs, FirstQuart, Median, ThirdQuart)).ToArray();

            Tree = GenerateBestTree(input, output);

            if(Tree == null)
            {
                return;
            }

            //Calculate Accuracy
            double Accuracy = 0;
            var ValidationSet = TrainingData.Skip((int)(TrainingData.Count * 0.8));

            foreach (var In in ValidationSet)
            {
                int computedClass = Tree.Decide(In.Inputs.ToArray());
                int origClass = ClassifyOutputs(In.Outputs, FirstQuart, Median, ThirdQuart);

                Accuracy += Math.Abs(computedClass - origClass) * (1.0 / (TotalClasses - 1));
            }

            NetworkAccuracy = 1 - (Accuracy / ValidationSet.Count());

            if (NetworkAccuracy >= 0.75)
            {
                foreach (var Indiv in Population)
                {
                    //var Result = Predict(Indiv.Vector);
                    int PredictedClass = Tree.Decide(Indiv.Vector.ToArray());
                    if (PassesThresholdCheck(PredictedClass) && Indiv.Fitness < 0) // 0 -> (0,FirstQuart); 1 -> (FirstQuart,Median); 2 -> (Median,ThirdQuart); 3 -> (ThirdQuart,Infinity)
                    {
                        var Result = Predict(Indiv.Vector);
                        Indiv.Fitness = Result.Sum();
                        Indiv.ObjectivesFitness = new List<double>(Result);
                        Indiv.Predicted = true;
                        IncrementPredictionCount(Generation, true);
                    }
                }
            }
        }

        public bool PassesThresholdCheck(int Class)
        {
            return (Class >= ThresholdClass);
            /*switch (ThresholdClass)
            {
                case 0:
                    return true;
                case 1:
                    return Fitness > FirstQuart;
                case 2:
                    return Fitness > Median;
                case 3:
                    return Fitness > ThirdQuart;
                default:
                    return false;
            }*/
        }

        public override List<double> Predict(List<double> Input)
        {
            int Result = 0;
            lock (NetworkLock)
            {
                Result = Tree.Decide(Input.ToArray());
            }
            /*switch (Result)
            {
                case 0:
                    return new List<double>() { FirstQuart - 1 };
                case 1:
                    return new List<double>() { Median - 1 };
                case 2:
                    return new List<double>() { ThirdQuart - 1 };
                default:
                    return new List<double>() { ThirdQuart + 1 };
            }*/

            if(Result == 0)
                return new List<double>() { FirstQuart - 1 };
                
            if(Result < ((double)TotalClasses - 1) / 2)
            {
                double Dif = Median - FirstQuart;
                return new List<double>() { FirstQuart + Dif * ((double)Result / (int)((TotalClasses - 1) / 2)) -1 };
            }

            if(Result < TotalClasses-1)
            {
                double Dif = ThirdQuart - Median;
                return new List<double>() { Median + Dif * (((double)Result - ((int)((TotalClasses - 1) / 2))) / (int)((TotalClasses - 1) / 2)) - 1 };
            }

            return new List<double>() { ThirdQuart + 1 };
            
        }

        
    }
}