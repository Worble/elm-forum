module Styles exposing (Styles(..), stylesheet)

import Color exposing (..)
import Style exposing (..)
import Style.Border as Border
import Style.Color as Color
import Style.Font as Font
import Style.Scale as Scale


type Styles
    = None
    | Title
    | Link
    | PostHeader
    | Post


scale : Int -> Float
scale =
    Scale.modular 16 1.618


stylesheet : StyleSheet Styles variation
stylesheet =
    Style.styleSheet
        [ style None []
        , style Title
            [ Font.size (scale 2)
            , Font.bold
            ]
        , style Link
            [ Font.size (scale 1)
            , Color.text Color.darkBlue
            , hover
                [ Font.underline
                ]
            ]
        , style PostHeader
            [ Border.bottom 1
            , Border.solid
            ]
        , style Post
            [ Color.background Color.lightGray

            ]
        ]
