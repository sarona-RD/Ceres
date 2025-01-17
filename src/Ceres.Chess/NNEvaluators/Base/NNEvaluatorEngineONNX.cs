#region License notice

/*
  This file is part of the Ceres project at https://github.com/dje-dev/ceres.
  Copyright (C) 2020- by David Elliott and the Ceres Authors.

  Ceres is free software under the terms of the GNU General Public License v3.0.
  You should have received a copy of the GNU General Public License
  along with Ceres. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

#region Using directives

using System;
using System.Buffers;

using Ceres.Chess.LC0NetInference;
using Ceres.Base;
using Ceres.Chess.NNEvaluators;
using Ceres.Chess.NetEvaluation.Batch;
using Ceres.Chess.LC0.Batches;
using Ceres.Base.Benchmarking;
using Ceres.Base.DataTypes;
using System.Collections.Generic;
using Ceres.Chess.MoveGen.Converters;
using Ceres.Chess.EncodedPositions.Basic;
using Ceres.Chess.MoveGen;
using Ceres.Chess;
using System.Diagnostics;
using Ceres.Chess.NNEvaluators.Defs;

#endregion

namespace Chess.Ceres.NNEvaluators
{
  /// <summary>
  /// NNEvaluator subclass which reads network definitions from ONNX file
  /// via the ONNX Runtime (using ONNXRuntimeExecutor).
  /// </summary>
  public class NNEvaluatorEngineONNX : NNEvaluator
  {
    /// <summary>
    /// Name of file containing ONNX network defition.
    /// </summary>
    public readonly string ONNXFileName;

    /// <summary>
    /// Batch size to be used with this evaluator.
    /// </summary>
    public readonly int BatchSize;

    /// <summary>
    /// Type of ONNX network.
    /// </summary>
    public readonly ONNXRuntimeExecutor.NetTypeEnum Type;


    /// <summary>
    /// Precision of network.
    /// </summary>
    public readonly NNEvaluatorPrecision Precision;

    /// <summary>
    /// Type of hardware device.
    /// </summary>
    public readonly NNDeviceType DeviceType;

    /// <summary>
    /// Executor object to run ONNX network evaluation.
    /// </summary>
    public readonly ONNXRuntimeExecutor Executor;


    /// <summary>
    /// Types of input(s) required by the evaluator.
    /// </summary>
    public override InputTypes InputsRequired => InputTypes.Positions | InputTypes.Boards | InputTypes.Moves;


    /// <summary>
    /// If the network contains a WDL (win/draw/loss) style value head.
    /// </summary>
    public override bool IsWDL => isWDL;

    /// <summary>
    /// If the network contains a MLH (moves left head).
    /// </summary>
    public override bool HasM => hasM;

    /// <summary>
    /// If the network contains an uncertanity of V head.
    /// </summary>
    public override bool HasUncertaintyV => hasUncertaintyV;

    readonly bool isWDL;
    readonly bool hasM;
    readonly bool hasUncertaintyV;


    /// <summary>
    /// Name of policy output slot.
    /// </summary>
    public readonly string OutputPolicy;

    /// <summary>
    /// Name of value output slot (if non-WDL).
    /// </summary>
    public readonly string OutputValue;

    /// <summary>
    /// Name of WDL output slot (if WDL).
    /// </summary>
    public readonly string OutputWDL;

    /// <summary>
    /// Name of MLH output slot.
    /// </summary>
    public readonly string OutputMLH;

    /// <summary>
    /// If the output of the value head are logistic values (otherwise straight probabilities).
    /// </summary>
    public readonly bool ValueHeadLogistic;

    /// <summary>
    /// If the 50 move plane should be scaled down by 99.
    /// </summary>
    public readonly bool Scale50MoveCounter;

    /// <summary>
    /// If MoveRecord should be initialized.
    /// </summary>
    public readonly bool MovesEnabled;

#if NOT
#endif
#region Statics

    // TODO: clean up this lookaside buffering
    static string lastONNXFileName;
    static long lastONNXBytesHash;
    static int lastBatchSize;
    static NNDeviceType lastDeviceType;
    static bool lastIsWDL;
    static bool lastUseTRT;
    static NNEvaluatorPrecision lastPrecision;
    static ONNXRuntimeExecutor lastExecutor;
    static ONNXRuntimeExecutor.NetTypeEnum lastType;

    #endregion

    private static long ByteArrayHash(byte[] byteArray)

    {
      long hash = 0;
      for (int i = 0; i < byteArray.Length; i++)
      {
        hash = (hash << 8) + byteArray[i];
        long g = hash & 0xF0000000L;
        if (g != 0)
        {
          hash ^= g >> 24;
          hash ^= g;
        }
      }
      return hash;
    } 

    public const int TPG_MODE_TOTAL_BYTES_ASSUMED  = 4060 + 782; // see DoEvaluateIntoBuffers

    public NNEvaluatorEngineONNX(string engineID, string onnxModelFileName, byte[] onnxModelBytes, NNDeviceType deviceType, int gpuID, bool useTRT,
                                 ONNXRuntimeExecutor.NetTypeEnum type, int batchSize,
                                 NNEvaluatorPrecision precision, bool isWDL, bool hasM, bool hasUncertaintyV,
                                 string outputValue, string outputWDL, string outputPolicy, string outputMLH, 
                                 bool valueHeadLogistic, bool scale50MoveCounter, bool movesEnabled = false, bool enableProfiling = false)
    {
      EngineType = type == ONNXRuntimeExecutor.NetTypeEnum.Ceres ? "ONNX_DJE" : "ONNX_LZ0";
      EngineNetworkID = engineID;
      ONNXFileName = onnxModelFileName;
      BatchSize = batchSize;
      Precision = precision;
      this.isWDL = isWDL;
      this.hasM = hasM;
      this.hasUncertaintyV = hasUncertaintyV;
      DeviceType = deviceType;
      OutputValue = outputValue;
      OutputWDL = outputWDL;
      OutputPolicy = outputPolicy;
      OutputMLH = outputMLH;
      ValueHeadLogistic = valueHeadLogistic;
      Scale50MoveCounter = scale50MoveCounter;
      MovesEnabled = movesEnabled;

      bool filesMatch = lastONNXFileName != null && lastONNXFileName == onnxModelFileName;
      bool bytesMatch = onnxModelBytes != null && ByteArrayHash(onnxModelBytes) == lastONNXBytesHash;

      const bool TRY_REUSE = false; // TODO: remove this completely, it is unsafe (?)
      if (TRY_REUSE && (filesMatch || bytesMatch) && lastBatchSize == batchSize && precision == lastPrecision
        && lastIsWDL == isWDL && lastType == type && deviceType == lastDeviceType && lastUseTRT == useTRT)
      {
        Executor = lastExecutor;
      }
      else
      {
        Console.WriteLine("Starting ONNX runtime against " + engineID + " from " + onnxModelFileName + " with GPU " + gpuID);

        Executor = new ONNXRuntimeExecutor(onnxModelFileName, onnxModelBytes, batchSize, type, precision, deviceType, gpuID, useTRT, enableProfiling);
        lastONNXFileName = onnxModelFileName;
        lastONNXBytesHash = onnxModelBytes == null ? 0 : ByteArrayHash(onnxModelBytes);
        lastDeviceType = deviceType;
        lastBatchSize = batchSize;
        lastIsWDL = isWDL;
        lastType = type;
        lastPrecision = precision;
        lastUseTRT = useTRT;
        lastExecutor = Executor;
      }
    }



    public static Action<IEncodedPositionBatchFlat, bool, float[], float[]> ConverterToFlat = null;
    public static Action<object, float[], float[]> ConverterToFlatFromTPG = null;

    [ThreadStatic] static float[] inputsPrimaryNative;
    [ThreadStatic] static float[] inputsSecondaryNative;

    /// <summary>
    /// Optional worker method which evaluates batch of positions which are already converted into native format needed by evaluator.
    /// </summary>
    /// <param name="positionsNativeInput"></param>
    /// <param name="usesSecondaryInputs"></param>
    /// <param name="retrieveSupplementalResults"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public override IPositionEvaluationBatch DoEvaluateNativeIntoBuffers(object positionsNativeInput, bool usesSecondaryInputs, 
                                                                         int numPositions, Func<int, int, bool> posMoveIsLegal,
                                                                         bool retrieveSupplementalResults = false)
    {
      Debug.Assert(!retrieveSupplementalResults);

      if (Executor.NetType != ONNXRuntimeExecutor.NetTypeEnum.TPG)
      {
        throw new Exception("DoEvaluateNativeIntoBuffers only supported for TPG net type.");
      }

      if (ConverterToFlatFromTPG == null)
      {
        throw new Exception("ConverterToFlatFromTPG must be provided");
      }

      if (inputsPrimaryNative == null)
      {
        // TO DO: Make smarter, check numPositions argument to smart size if possible (instead we make some guesses here on size, assume batch size 
        const int INPUT_SIZE_FLOATS = 2048 * 100 * 128; 
        inputsPrimaryNative = new float[INPUT_SIZE_FLOATS];
        inputsSecondaryNative = new float[INPUT_SIZE_FLOATS];
      }

      ConverterToFlatFromTPG(positionsNativeInput, inputsPrimaryNative, usesSecondaryInputs ? inputsSecondaryNative : null);
      PositionEvaluationBatch ret = DoEvaluateBatch(default, inputsPrimaryNative, usesSecondaryInputs ? inputsSecondaryNative : null, 
                                                    numPositions, retrieveSupplementalResults, posMoveIsLegal);
      return ret;
    }


    /// <summary>
    /// Overrides worker method to evaluate a specified batch into internal buffers.
    /// </summary>
    /// <param name="batch"></param>
    /// <param name="retrieveSupplementalResults"></param>
    /// <returns></returns>
    public override IPositionEvaluationBatch DoEvaluateIntoBuffers(IEncodedPositionBatchFlat batch, bool retrieveSupplementalResults = false)
    {
      if (Executor.NetType == ONNXRuntimeExecutor.NetTypeEnum.TPG)
      {
        if (ConverterToFlat == null)
        {
          throw new Exception("ConverterToFlat must be provided");
        }

        int inputSizeAttention = batch.NumPos * 64 * ONNXRuntimeExecutor.TPG_BYTES_PER_SQUARE_RECORD;
        float[] flatValuesAttention = ArrayPool<float>.Shared.Rent(inputSizeAttention);

        int inputSizeMoves = 0; // batch.NumPos * 782;
        float[] flatValuesMoves = null;// ArrayPool<float>.Shared.Rent(batch.NumPos * 782);

        Memory<float> flatValuesAttentionM = flatValuesAttention.AsMemory().Slice(0, inputSizeAttention);
        Memory<float> flatValuesMovesM = flatValuesMoves == null ? default : flatValuesMoves.AsMemory().Slice(0, inputSizeMoves);

        ConverterToFlat(batch, MovesEnabled, flatValuesAttention, flatValuesMoves);
        PositionEvaluationBatch ret = DoEvaluateBatch(batch, flatValuesAttentionM, flatValuesMoves, batch.NumPos, retrieveSupplementalResults);
        Debug.Assert(!retrieveSupplementalResults);
        return ret;
      }
      else
      {
        int bufferLength = 112 * batch.NumPos * 64;
        float[] flatValues = ArrayPool<float>.Shared.Rent(bufferLength);

        batch.ValuesFlatFromPlanes(flatValues, false, Scale50MoveCounter);
        PositionEvaluationBatch ret = DoEvaluateBatch(batch, flatValues, null, batch.NumPos, retrieveSupplementalResults);

        ArrayPool<float>.Shared.Return(flatValues);
        return ret;
      }
    }

    /// <summary>
    /// If this evaluator produces the same output as another specified evaluator.
    /// </summary>
    /// <param name="evaluator"></param>
    /// <returns></returns>
    public override bool IsEquivalentTo(NNEvaluator evaluator)
    {      
      return evaluator is NNEvaluatorEngineONNX
          && ((NNEvaluatorEngineONNX)evaluator).EngineNetworkID == EngineNetworkID;
    }

    /// <summary>
    /// The maximum number of positions that can be evaluated in a single batch.
    /// </summary>
    public override int MaxBatchSize => BatchSize;


#region Internals

    /// <summary>
    /// Internal worker method to 
    /// </summary>
    /// <param name="flatValuesPrimary"></param>
    /// <param name="numPos"></param>
    /// <param name="retrieveSupplementalResults"></param>
    /// <returns></returns>
    PositionEvaluationBatch DoEvaluateBatch(IEncodedPositionBatchFlat batch, 
                                            Memory<float> flatValuesPrimary, Memory<float> flatValuesSecondary,
                                            int numPos, bool retrieveSupplementalResults, 
                                            Func<int,int, bool> posMoveIsLegal = null)
    {
      if (retrieveSupplementalResults) throw new Exception("retrieveSupplementalResults not supported");

      ONNXRuntimeExecutorResultBatch result;
      TimingStats stats = new TimingStats();
//      using (new TimingBlock(stats, TimingBlock.LoggingType.None))
      {
        lock (Executor)
        {
          result = Executor.Execute(IsWDL, flatValuesPrimary, flatValuesSecondary, numPos, alreadyConvertedToLZ0: true);

          // Apply move masking
          if (posMoveIsLegal != null) 
          {
            for (int i = 0; i < numPos; i++)
            {
              for (int j = 0; j < 1858; j++)
              {
                if (!posMoveIsLegal(i,j))
                {
                  result.PolicyVectors[i][j] = -100f;
                }
              }
            }
          }
#if NO_LONGER_NEEDED_NOT_USING_96_FORMAT
          if (Executor.NetType == ONNXRuntimeExecutor.NetTypeEnum.TPG)
          {
            ConvertTPGPolicyToExpanded(batch, result);
          }
#endif
        }
      }

      FP16[] mFP16 = null;
      if (HasM)
      {
        if (result.MLH == null)
        {
          throw new Exception("ONNX evaluator was created with MLH argument true but network does not appear to contain MLH head: " + EngineNetworkID);
        }
        mFP16 = Array.ConvertAll<float, FP16>(result.MLH, m => (FP16)m);
      }

      FP16[] uncertaintyVFP16 = null;
      if (HasUncertaintyV)
      {
        if (result.UncertaintyV == null)
        {
          throw new Exception("ONNX evaluator was created with UV argument true but network does not appear to contain uncertainty of V head: " + EngineNetworkID);
        }
        uncertaintyVFP16 = Array.ConvertAll<float, FP16>(result.UncertaintyV, uv => (FP16)uv);
      }

#if DONE_BELOW_IN_NEXT_LINE
      // Set probability of illegal moves to 0.
      HashSet<int> legalIndices = new HashSet<int>(96);
      for (int pos=0; pos<numPos;pos++)
      {
        legalIndices.Clear();
        for (int i = 0; i <batch.Moves[pos].NumMovesUsed;i++)
        {
          EncodedMove encodedMove  = ConverterMGMoveEncodedMove.MGChessMoveToEncodedMove(batch.Moves[pos].MovesArray[i]);
          legalIndices.Add(encodedMove.IndexNeuralNet);
        }

        for (int i=0;i<1858;i++)
        {
          if (!legalIndices.Contains(i))
          {
            result.PolicyVectors[pos][i] = -1E10f;
          }
        }
      }
#endif

      // NOTE: inefficient, above we convert from [] (flat) to [][] and here we convert back to []
      return new PositionEvaluationBatch(IsWDL, HasM, HasUncertaintyV, numPos, result.ValuesRaw, result.PolicyFlat, 
                                         mFP16, uncertaintyVFP16, null, ValueHeadLogistic,
                                         PositionEvaluationBatch.PolicyType.LogProbabilities, false, batch, stats);
    }

#endregion


#if NOT
if ((transform & FlipTransform) != 0) {
    sq.set(sq.row(), 7 - sq.col());
  }
  if ((transform & MirrorTransform) != 0) {
    sq.set(7 - sq.row(), sq.col());
  }
  if ((transform & TransposeTransform) != 0) {
    sq.set(7 - sq.col(), 7 - sq.row());
  }
  return sq;
}
#endif
    void ConvertTPGPolicyToExpanded(IEncodedPositionBatchFlat batch, ONNXRuntimeExecutorResultBatch result)
    {
      Span<MGMoveList> allMoves = batch.Moves;
      for (int i=0; i<batch.NumPos;i++)
      {
        // TODO: Very inefficient - create many arrays
        float[] policyVectorSource = result.PolicyVectors[i];
        float[] policyVectorTarget = new float[1858];

        MGMoveList moves = allMoves[i];
        for (int m=0; m<moves.NumMovesUsed;m++)
        {
//          Console.WriteLine(moves.MovesArray[m] + " " + moves.MovesArray[m].Reversed);
          EncodedMove move = ConverterMGMoveEncodedMove.MGChessMoveToEncodedMove(moves.MovesArray[m]);
          
//          move = new EncodedMove(move.FromSquare.Flipped, move.ToSquare.Flipped, move.Promotion, move.IsCastling);
          
          //EncodedMove moveOther = new EncodedMove(1857 - move.RawValue);
//          Console.WriteLine(moveOther);
//          if (batch.Positions[i].SideToMove == SideType.Black)  move = move.Mirrored.Flipped;
          int index = move.IndexNeuralNet;
          policyVectorTarget[index] = policyVectorSource[m];
        }

        // Rewrite with expanded policy vector just created
        result.PolicyVectors[i] = policyVectorTarget;
      }
    }

    public void EndProfiling()
    {
      Executor.EndProfiling();
    }


    protected override void DoShutdown()
    {
      Executor.Dispose();
    }

  }

}
