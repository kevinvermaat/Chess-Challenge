using System;
using System.Linq;
using ChessChallenge.API;

/*
 * So here we go, 8 august, let's start from scratch, let's name this project NebulaAI
 * V1: NegaMax, Q Search, Move ordering, Piece Square Tables and Transposition Tables
 * V2: Null move pruning, History heuristics
 */
public class V2 : IChessBot
{
    // Transposition table (size is 2 ^ 22 = 4,194,304 entries)
    private record struct TtEntry(ulong Key, int Score, int Depth, int Flag, Move Move);
    private TtEntry[] _tt = new TtEntry[0x400000];

    // History Heuristics 
    public int[,,] HistoryHeuristics;

    // Globals
    Timer _searchTimer;

    // Keep track on the best move
    Move _bestMove;

    private int Evaluate(Board board)
    {
        int mg = 0, eg = 0, phase = 0, sideToMove = 2;
        for (; --sideToMove >= 0;)
        {
            for (int piece = -1; ++piece < 6;)
            for (ulong mask = board.GetPieceBitboard((PieceType)piece + 1, sideToMove > 0); mask != 0;)
            {
                // A number between 0 to 63 that indicates which square the piece is on, flip for black
                int squareIndex = BitboardHelper.ClearAndGetIndexOfLSB(ref mask) ^ 56 * sideToMove;

                // Piece values are baked into the pst (see constructor of the bot)
                mg += _pst[squareIndex][piece];
                eg += _pst[squareIndex][piece + 6];

                // The less pieces, the more we bend towards our endgame strategy
                phase += _phaseWeight[piece];
            }

            // Flip score for optimised token count (always white perspective due to double flip)
            // Eg. White eval = 2300 -> flip -> -2300 -> black eval = 2000 -> -300 -> flip -> 300 
            mg = -mg;
            eg = -eg;
        }

        // Tapered evaluation since our goals towards endgame shifts
        return (mg * phase + eg * (24 - phase)) / 24 * (board.IsWhiteToMove ? 1 : -1);
    }

    public int Search(Board board, int depth, int ply, int alpha, int beta, bool canNullMove = true)
    {
        // Search variables
        int alphaStart = alpha,
            bestScore = -100000,
            currentTurn = board.IsWhiteToMove ? 1 : 0;
        bool root = ply == 0,
            inCheck = board.IsInCheck(),
            qSearch = depth < 1;
        Move bestMove = Move.NullMove;

        // Check for repetition since TT doesn't know that and we don't want draws when we can win
        if (!root && board.IsRepeatedPosition()) return 0; // TODO: maybe use here -100;

        // Try to find the board position in the tt
        ulong key = board.ZobristKey;
        TtEntry ttEntry = _tt[key % 0x400000]; // Todo: deconstruct to save tokens

        // When we find the transposition check if we can use it to narrow our alpha beta bounds
        if (!root && ttEntry.Key == key && ttEntry.Depth >= depth)
        {
            // 1 = lower bound; 2 = exact; 3 = upper bound
            if (ttEntry.Flag == 2) return ttEntry.Score;
            if (ttEntry.Flag == 1) alpha = Math.Max(alpha, ttEntry.Score);
            if (ttEntry.Flag == 3) beta = Math.Min(beta, ttEntry.Score);

            // Beta cutoff when there is an established better branch that resulted in the alpha score
            if (beta <= alpha) return ttEntry.Score;
        }

        // Search quiescence position to prevent horizon effect of depth first search
        if (qSearch)
        {
            bestScore = Evaluate(board);
            if (beta <= bestScore) return bestScore;
            alpha = Math.Max(alpha, bestScore);
        }
        // Null move pruning
        else if (canNullMove && !inCheck)
        {
            board.ForceSkipTurn();
            // Depth - (1 + R, let's use 2 since everyone online seems to agree with that)
            int nullMoveEval = -Search(board, depth - 3, ply + 1, -beta, -beta + 1, false);
            board.UndoSkipTurn();

            // Prune branch when the side who got a free move can't even improve
            if (beta <= nullMoveEval) return nullMoveEval;
        }

        // Move Ordering
        var moves = board.GetLegalMoves(qSearch).OrderByDescending(
            move =>
                // Best move at transposition
                move == ttEntry.Move ? 1000000 :
                // MVV-LVA
                move.IsCapture ? 100000 * (int)move.CapturePieceType - (int)move.MovePieceType :
                // Promotions
                move.IsPromotion ? 100000 :
                // History Heuristics
                HistoryHeuristics[currentTurn, (int)move.MovePieceType, move.TargetSquare.Index]
        ).ToArray();

        foreach (Move move in moves)
        {
            if (_searchTimer.MillisecondsElapsedThisTurn > _searchTimer.MillisecondsRemaining / 40) return 100000;
            
            board.MakeMove(move);
            int score = -Search(board, depth - 1, ply + 1, -beta, -alpha);
            board.UndoMove(move);

            if (score > bestScore)
            {
                if (ply == 0) _bestMove = move;

                bestMove = move;
                bestScore = score;
                alpha = Math.Max(alpha, bestScore);

                // Beta cutoff when there is an established better branch that resulted in the alpha score
                if (beta <= alpha)
                {
                    if (!qSearch && !move.IsCapture) 
                        HistoryHeuristics[currentTurn, (int)move.MovePieceType, move.TargetSquare.Index] += depth * depth;
                    break;
                }
            }
        }

        // Efficient way to check for checkmate which is faster (nps)
        if (!qSearch && moves.Length == 0) return board.IsInCheck() ? -100000 + ply : 0;

        // Decide the current search bounds so we're able to properly check if we're allowed to cutoff later
        int flag = bestScore <= alphaStart ? 3 : bestScore >= beta ? 1 : 2;

        // Store the position and it's eval to the transposition table for fast lookup when same position is found twice
        _tt[key % 0x400000] = new TtEntry(key, bestScore, depth, flag, bestMove);

        return bestScore;
    }

    public Move Think(Board board, Timer timer)
    {
        _searchTimer = timer;

        // History heuristics
        HistoryHeuristics = new int[2, 7, 64];

        // Reset to prevent lingering previous moves
        _bestMove = Move.NullMove;

        // Iterative deepening
        for (int depth = 1; depth < 50; depth++)
        {
            Search(board, depth, 0, -100000, 100000, true);
            
            if (_searchTimer.MillisecondsElapsedThisTurn > _searchTimer.MillisecondsRemaining / 40) break;
        }
        
        return _bestMove.IsNull ? board.GetLegalMoves()[0] : _bestMove;
    }

    // 
    // PST Packer and Un-packer - credits to Tyrant
    readonly int[] _phaseWeight = { 0, 1, 1, 2, 4, 0 };

    // Pawn, Knight, Bishop, Rook, Queen, King 
    private readonly short[] _pieceValues =
    {
        82, 337, 365, 477, 1025, 20000,
        94, 281, 297, 512, 936, 20000
    };

    private readonly decimal[] _packedPst =
    {
        63746705523041458768562654720m, 71818693703096985528394040064m, 75532537544690978830456252672m,
        75536154932036771593352371712m, 76774085526445040292133284352m, 3110608541636285947269332480m,
        936945638387574698250991104m, 75531285965747665584902616832m, 77047302762000299964198997571m,
        3730792265775293618620982364m, 3121489077029470166123295018m, 3747712412930601838683035969m,
        3763381335243474116535455791m, 8067176012614548496052660822m, 4977175895537975520060507415m,
        2475894077091727551177487608m, 2458978764687427073924784380m, 3718684080556872886692423941m,
        4959037324412353051075877138m, 3135972447545098299460234261m, 4371494653131335197311645996m,
        9624249097030609585804826662m, 9301461106541282841985626641m, 2793818196182115168911564530m,
        77683174186957799541255830262m, 4660418590176711545920359433m, 4971145620211324499469864196m,
        5608211711321183125202150414m, 5617883191736004891949734160m, 7150801075091790966455611144m,
        5619082524459738931006868492m, 649197923531967450704711664m, 75809334407291469990832437230m,
        78322691297526401047122740223m, 4348529951871323093202439165m, 4990460191572192980035045640m,
        5597312470813537077508379404m, 4980755617409140165251173636m, 1890741055734852330174483975m,
        76772801025035254361275759599m, 75502243563200070682362835182m, 78896921543467230670583692029m,
        2489164206166677455700101373m, 4338830174078735659125311481m, 4960199192571758553533648130m,
        3420013420025511569771334658m, 1557077491473974933188251927m, 77376040767919248347203368440m,
        73949978050619586491881614568m, 77043619187199676893167803647m, 1212557245150259869494540530m,
        3081561358716686153294085872m, 3392217589357453836837847030m, 1219782446916489227407330320m,
        78580145051212187267589731866m, 75798434925965430405537592305m, 68369566912511282590874449920m,
        72396532057599326246617936384m, 75186737388538008131054524416m, 77027917484951889231108827392m,
        73655004947793353634062267392m, 76417372019396591550492896512m, 74568981255592060493492515584m,
        70529879645288096380279255040m,
    };

    private readonly int[][] _pst;

    // Constructor and wizardry to unpack the bitmap piece square tables and bake the piece values into the values
    public V2()
    {
        _pst = _packedPst.Select(packedTable =>
        {
            int pieceType = 0;
            return decimal.GetBits(packedTable).Take(3)
                .SelectMany(bit => BitConverter.GetBytes(bit)
                    .Select(square => (int)((sbyte)square * 1.461) + _pieceValues[pieceType++]))
                .ToArray();
        }).ToArray();
    }
}