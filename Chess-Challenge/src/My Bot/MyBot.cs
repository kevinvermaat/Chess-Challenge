﻿using ChessChallenge.API;

public class MyBot : IChessBot
{
    public Move Think(Board board, Timer timer)
    {
        return board.GetLegalMoves()[0];
    }
}