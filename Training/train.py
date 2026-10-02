"""Compatibility alias for the ordinary ML-Agents CLI.
The fielder trainer is selected by YAML and registered by baseball-mlagents.
Preferred command: mlagents-learn Training/config/stage3_full_team.yaml [options].
"""

if __name__ == "__main__":
    from mlagents.trainers.learn import main
    main()
